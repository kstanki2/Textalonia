using System.Globalization;
using System.Reflection;
using Textalonia.Model;

namespace Textalonia.Baselines;

internal static class PublicApi
{
    public static string Capture()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var lines = new List<string>();
        foreach (var type in typeof(FlowDocument).Assembly.GetExportedTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            lines.Add($"type {Name(type)} : {Name(type.BaseType)}" + (type.IsSealed ? " sealed" : "") + (type.IsAbstract ? " abstract" : ""));
            lines.AddRange(type.GetInterfaces().Select(t => $"  interface {Name(t)}").Order(StringComparer.Ordinal));
            var members = new List<string>();
            foreach (var c in type.GetConstructors(flags).Where(Visible)) members.Add($"ctor {Access(c)} ({Parameters(c)})");
            foreach (var m in type.GetMethods(flags).Where(m => Visible(m) && !m.IsSpecialName))
                members.Add($"method {Access(m)} {Name(m.ReturnType)} {m.Name}({Parameters(m)})" + (m.IsVirtual ? " virtual" : ""));
            foreach (var p in type.GetProperties(flags).Where(p => p.GetAccessors(true).Any(Visible)))
                members.Add($"property {Name(p.PropertyType)} {p.Name} {{ " + string.Join(" ", p.GetAccessors(true).Where(Visible).Select(m => $"{Access(m)} {(m.Name.StartsWith("get_", StringComparison.Ordinal) ? "get" : "set")};")) + " }");
            foreach (var f in type.GetFields(flags).Where(f => f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly))
                members.Add($"field {Name(f.FieldType)} {f.Name}" + (f.IsStatic ? " static" : "") + (f.IsInitOnly ? " readonly" : "") + (f.IsLiteral ? $" = {Convert.ToString(f.GetRawConstantValue(), CultureInfo.InvariantCulture)}" : ""));
            foreach (var e in type.GetEvents(flags).Where(e => e.AddMethod is { } m && Visible(m)))
                members.Add($"event {Name(e.EventHandlerType)} {e.Name}");
            lines.AddRange(members.Order(StringComparer.Ordinal).Select(m => "  " + m));
        }
        return string.Join("\n", lines) + "\n";
    }

    private static bool Visible(MethodBase method) => method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly;
    private static string Access(MethodBase method) => (method.IsPublic ? "public" : "protected") + (method.IsStatic ? " static" : "");
    private static string Parameters(MethodBase method) => string.Join(", ", method.GetParameters().Select(p =>
        $"{Name(p.ParameterType)} {p.Name}" + (p.IsOptional ? $" = {Convert.ToString(p.DefaultValue, CultureInfo.InvariantCulture) ?? "null"}" : "")));
    private static string Name(Type? type) => type is null ? "none" : type.IsGenericType
        ? type.GetGenericTypeDefinition().FullName!.Split('`')[0] + "<" + string.Join(",", type.GetGenericArguments().Select(Name)) + ">"
        : type.FullName ?? type.Name;
}
