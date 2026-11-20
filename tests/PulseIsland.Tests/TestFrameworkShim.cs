#if !USE_XUNIT_PACKAGE
using System.Reflection;

namespace Xunit;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class FactAttribute : Attribute
{
}

public static class Assert
{
    public static void True(bool condition, string? userMessage = null)
    {
        if (!condition)
            throw new Exception(userMessage ?? "Assert.True failed.");
    }

    public static void False(bool condition, string? userMessage = null)
    {
        if (condition)
            throw new Exception(userMessage ?? "Assert.False failed.");
    }

    public static void Null(object? value)
    {
        if (value is not null)
            throw new Exception($"Assert.Null failed. Actual: {value}");
    }

    public static void NotNull(object? value)
    {
        if (value is null)
            throw new Exception("Assert.NotNull failed.");
    }

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"Assert.Equal failed. Expected: '{expected}', Actual: '{actual}'.");
    }

    public static void NotEqual<T>(T notExpected, T actual)
    {
        if (EqualityComparer<T>.Default.Equals(notExpected, actual))
            throw new Exception($"Assert.NotEqual failed. Both values were: '{actual}'.");
    }

    public static void Contains(string expectedSubstring, string actualString, StringComparison comparison = StringComparison.Ordinal)
    {
        if (actualString is null || !actualString.Contains(expectedSubstring, comparison))
            throw new Exception($"Assert.Contains failed. '{expectedSubstring}' not found in '{actualString}'.");
    }

    public static void DoesNotContain(string forbiddenSubstring, string actualString, StringComparison comparison = StringComparison.Ordinal)
    {
        if (actualString is not null && actualString.Contains(forbiddenSubstring, comparison))
            throw new Exception($"Assert.DoesNotContain failed. Found '{forbiddenSubstring}' in '{actualString}'.");
    }
}

internal static class OfflineTestRunnerProgram
{
    public static async Task<int> Main()
    {
        Console.WriteLine("=== PulseIsland Unit & Integration Test Suite ===");
        int passed = 0;
        int failed = 0;

        var testTypes = Assembly.GetExecutingAssembly()
            .GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .OrderBy(t => t.Name);

        foreach (var type in testTypes)
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.GetCustomAttribute<FactAttribute>() is not null)
                .OrderBy(m => m.Name)
                .ToList();

            if (methods.Count == 0) continue;

            foreach (var method in methods)
            {
                string testName = $"{type.Name}.{method.Name}";
                try
                {
                    var instance = Activator.CreateInstance(type);
                    var result = method.Invoke(instance, null);
                    if (result is Task task)
                        await task;

                    Console.WriteLine($"  [PASS] {testName}");
                    passed++;
                }
                catch (Exception ex)
                {
                    var rootEx = ex is TargetInvocationException tie && tie.InnerException is not null
                        ? tie.InnerException
                        : ex;
                    Console.WriteLine($"  [FAIL] {testName} -> {rootEx.GetType().Name}: {rootEx.Message}");
                    failed++;
                }
            }
        }

        Console.WriteLine($"=== Summary: {passed} passed, {failed} failed, {passed + failed} total ===");
        return failed == 0 ? 0 : 1;
    }
}
#endif
