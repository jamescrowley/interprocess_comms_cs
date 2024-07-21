using System.Text.Json;
using InterprocessComms;
using NUnit.Framework;

namespace InterprocessComms.Tests;

class ChildProcessDelegatorTests
{
    private readonly ChildProcessDelegator<StaticClass> _childProcess = new(() => new StaticClass(), Console.WriteLine);

    [OneTimeSetUp]
    public async Task Start()
    {
        await _childProcess.Start();
    }
    [OneTimeTearDown]
    public async Task Terminate()
    {
        await _childProcess.Terminate();
    }
    [Test]
    public async Task RunsAsExpected()
    {
        var aVariable = 4;
        var result = await _childProcess.Invoke( a=> a.DoSomething(4, new StaticClass()));
        Assert.That(result, Is.EqualTo(4));
    }
    
    [Test]
    public async Task RunsAsExpected2()
    {
        var result = await _childProcess.Invoke( a=> a.DoSomething(10, null));
        Assert.That(result, Is.EqualTo(14));
    }
    
    [Test]
    public async Task RunsAsExpected3()
    {
        await _childProcess.Terminate();
        await _childProcess.Start();
        var result = await _childProcess.Invoke( a => a.DoSomething(2, null));
        Assert.That(result, Is.EqualTo(2));
    }
}