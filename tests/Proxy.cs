using System.Reflection;

public class Proxy : DispatchProxy
{
    private System.Func<MethodInfo, object?[]?, object?> handler = null!;
    public static T Create<T>(System.Func<MethodInfo, object?[]?, object?> callback) where T : class
    { var value = Create<T, Proxy>(); ((Proxy)(object)value).handler = callback; return value; }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        object? result = handler(method!, args);
        return result ?? (method!.ReturnType.IsValueType && method.ReturnType != typeof(void) ? Activator.CreateInstance(method.ReturnType) : null);
    }
}
