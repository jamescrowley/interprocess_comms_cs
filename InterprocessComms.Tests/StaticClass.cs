namespace InterprocessComms.Tests;

public class StaticClass
{
    private static int _variable = 0;
    
    public int DoSomething(int arg, object? arg2)
    {
        Console.WriteLine($"got {arg} and {arg2}");
        _variable += arg;
        return _variable;
    }
}