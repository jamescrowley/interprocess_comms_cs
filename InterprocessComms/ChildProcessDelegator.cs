namespace InterprocessComms;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Serialize.Linq.Serializers;
using Tmds.Utils;
using JsonSerializer = System.Text.Json.JsonSerializer;

/// <summary>
/// This class enables a child process to be created (with their own static variable instances)
/// and marshals communication between the child process and the parent process
/// </summary>
/// <typeparam name="T">the target on which we invoke methods</typeparam>
public class ChildProcessDelegator<T>(
    Expression<Func<T>> invokeTargetFactoryExpression,
    Action<string> logger,
    params Type[] knownTypes)
    : IDisposable
{
    private readonly string _pipeName = Guid.NewGuid().ToString();
    private readonly ExpressionSerializer _expressionSerializer = CreateExpressionSerializer(knownTypes);

    private NamedPipeClientStream? _parentClientPipe;
    private Process? _childProcess;

    private static ExpressionSerializer CreateExpressionSerializer(IEnumerable<Type> knownTypes)
    {
        var jsonSerializer = new Serialize.Linq.Serializers.JsonSerializer();
        jsonSerializer.AddKnownTypes(knownTypes);
        return new ExpressionSerializer(jsonSerializer);
    }
    public async Task Start()
    {
        _parentClientPipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        // arguments can only be passed to child process in string form
        // so we serialize an expression to create the desired target class
        // in the child process
        _childProcess = ExecFunction.Start(async (args) =>
        {
            var pipeNameArg = args[0];
            var initTargetArg = args[1];
            var knownTypesArg = JsonSerializer.Deserialize<IEnumerable<Type>>(args[2]) ?? [];
            await ChildProcessWorker(pipeNameArg, initTargetArg, knownTypesArg);
        }, new[]
        {
            _pipeName, 
            _expressionSerializer.SerializeText(invokeTargetFactoryExpression), 
            JsonSerializer.Serialize(knownTypes.Select(t=>t.AssemblyQualifiedName))
        }, x=>
        {
            x.StartInfo.RedirectStandardError = true;
            x.StartInfo.RedirectStandardOutput = true;
        });
        _childProcess.OutputDataReceived += (_, outLine) =>
        {
            LogFromChildProcessOutput(outLine.Data ?? string.Empty);
        };
        _childProcess.ErrorDataReceived += (_, errorLine) =>
        {
            LogFromChildProcessOutput(errorLine.Data ?? string.Empty);
        };
        _childProcess.BeginOutputReadLine();
        _childProcess.BeginErrorReadLine();
        LogFromParentProcess($"Launched child process with pid {_childProcess.Id}");
        // this is where you can attach the debugger to the child process if required
        await _parentClientPipe.ConnectAsync();
        LogFromParentProcess($"Connected to child process with pid {_childProcess.Id}");
    }
    void LogFromParentProcess(string message)
    {
        logger($"parent {Process.GetCurrentProcess().Id}: {message}");
    }
    void LogFromChildProcessOutput(string message)
    {
        logger($"child  {_childProcess?.Id}: {message}");
    }
    private static async Task ChildProcessWorker(string pipeName, string invokeTargetFactoryExpression, IEnumerable<Type> knownTypes)
    {
        Console.WriteLine("Child process starting up...");
        // this may be counter-intuitive, but we're running the server on the child process
        // this is so we can pause and wait for the client (parent process) to connect
        // and allow us to attach a debugger before proceeding if required
        await using var childServerPipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte);
        Console.WriteLine("Waiting for parent process to connect...");
        // wait for the parent process to connect
        // this is where we can attach the debugger
        await childServerPipe.WaitForConnectionAsync();
        var expressionSerializer = CreateExpressionSerializer(knownTypes);
        var invokeTarget = CreateInvokeTargetUsingFactoryExpression(invokeTargetFactoryExpression, expressionSerializer);
        await using StreamWriter writer = new StreamWriter(childServerPipe, leaveOpen:true /* disposing of server pipe will close */ );
        writer.AutoFlush = true;
        using StreamReader reader = new StreamReader(childServerPipe, leaveOpen:true /* disposing of server pipe will close */ );
        while (!reader.EndOfStream)
        {
            var serializedExpression = await reader.ReadLineAsync();
            if (serializedExpression == null)
            {
                Console.WriteLine($"Received end of stream");
                break;
            }
            var deserializedExpression = expressionSerializer.DeserializeText(serializedExpression);
            Console.WriteLine($"Received command {deserializedExpression}");
            if (deserializedExpression == null) break;
        
            var response = ((LambdaExpression)deserializedExpression).Compile().DynamicInvoke(invokeTarget);
            var jsonResponse = JsonSerializer.Serialize(response);
            await writer.WriteLineAsync(jsonResponse);
        }
        Console.WriteLine($"Child process exiting...");
    }

    private static object CreateInvokeTargetUsingFactoryExpression(string invokeTargetFactoryExpression, ExpressionSerializer expressionSerializer)
    {
        var invokeTargetFactoryParsedExpression = (LambdaExpression)expressionSerializer.DeserializeText(invokeTargetFactoryExpression);
        var invokeTarget = invokeTargetFactoryParsedExpression.Compile().DynamicInvoke(null);
        if (invokeTarget == null)
            throw new InvalidOperationException($"Unable to create target object with {invokeTargetFactoryParsedExpression}");
        return invokeTarget;
    }

    public async Task<TResponse?> Invoke<TResponse>(Expression<Func<T,TResponse>> expr)
    {
        return await Invoke<TResponse>((Expression)expr);
    }
    
    public async Task Invoke(Expression<Action<T>> expr)
    {
        await Invoke<object?>(expr);
    }

    private async Task<TResponse?> Invoke<TResponse>(Expression expr)
    {
        if (_parentClientPipe == null)
            throw new InvalidOperationException("No pipe to send data to. Child process may not be running");
        LogFromParentProcess($"Sending command to child process with pid {_childProcess?.Id} {_parentClientPipe.IsConnected}");
        string serializedExpression = _expressionSerializer.SerializeText(expr);
        await using (var writer = new StreamWriter(_parentClientPipe, leaveOpen: true))
        {
            writer.AutoFlush = true;
            await writer.WriteLineAsync(serializedExpression);
        }

        string? jsonResponse;
        using (var reader = new StreamReader(_parentClientPipe, leaveOpen: true))
        {
            jsonResponse = await reader.ReadLineAsync();
        }
        if (jsonResponse == null)
            throw new InvalidOperationException("Received null response. Child process may not be running");
        var response = JsonSerializer.Deserialize<TResponse>(jsonResponse);
        LogFromParentProcess($"Received response: {response}");
        return response;
    }

    public async Task Terminate()
    {
        LogFromParentProcess($"Disconnecting from child process with pid {_childProcess?.Id}");
        _parentClientPipe?.Close();
        if (_parentClientPipe != null) await _parentClientPipe.DisposeAsync();
        LogFromParentProcess("Waiting for child process to exit...");
        if (_childProcess != null) await _childProcess.WaitForExitAsync();
        _parentClientPipe = null;
        _childProcess?.Dispose();
        _childProcess = null;
    }

    public void Dispose()
    {
        Task.Run(Terminate);
    }
}