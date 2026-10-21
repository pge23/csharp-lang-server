namespace HelloLib;

public sealed class Greeter
{
    private readonly IClock _clock;

    public Greeter(IClock clock)
    {
        _clock = clock;
    }

    public string Greet(string name)
    {
        var now = _clock.UtcNow;
        return $"Hello, {name}! The time is {now:O}.";
    }
}
