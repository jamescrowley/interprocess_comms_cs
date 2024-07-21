namespace InterprocessComms.Tests;

public class StaticClass
{
    private static int Variable = 0;
    
    public int DoSomething(int arg, object arg2)
    {
        Console.WriteLine($"got {arg} and {arg2}");
        Variable += arg;
        return Variable;
    }
}