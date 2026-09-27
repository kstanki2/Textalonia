using System.Globalization;
using System.Reflection;
using Textalonia.Model;

namespace Textalonia.Baselines;

internal static class PublicApi
{
    // Supplement the original signature baseline with source-contract metadata that reflection
    // signatures alone omit: nested nullability, init/ref/out, attributes and generic constraints.
    public static string CaptureContracts()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var nullability = new NullabilityInfoContext();
        var lines = new List<string>();
        foreach (var type in typeof(FlowDocument).Assembly.GetExportedTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            lines.Add($"type {Name(type)} {Attributes(type.CustomAttributes)} {Constraints(type.GetGenericArguments())}");
            var members = new List<string>();
            foreach (var member in type.GetMembers(flags))
            {
                switch (member)
                {
                    case MethodBase method when Visible(method):
                        var result = method is MethodInfo info
                            ? $"return {Nullability(nullability.Create(info.ReturnParameter))} {Modifiers(info.ReturnParameter)} {Attributes(info.ReturnParameter.CustomAttributes)}"
                            : "";
                        var parameters = string.Join(", ", method.GetParameters().Select(p =>
                            $"{p.Name}: {Nullability(nullability.Create(p))} in={p.IsIn} out={p.IsOut} {Modifiers(p)} {Attributes(p.CustomAttributes)}"));
                        members.Add($"{method.Name}({parameters}) {result} abstract={method.IsAbstract} virtual={method.IsVirtual} final={method.IsFinal} {Attributes(method.CustomAttributes)} " +
                            (method.IsGenericMethod ? Constraints(method.GetGenericArguments()) : ""));
                        break;
                    case PropertyInfo property when property.GetAccessors(true).Any(Visible):
                        members.Add($"property {property.Name} {Nullability(nullability.Create(property))} {Attributes(property.CustomAttributes)}");
                        break;
                    case FieldInfo field when field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly:
                        members.Add($"field {field.Name} {Nullability(nullability.Create(field))} {Attributes(field.CustomAttributes)}");
                        break;
                    case EventInfo ev when ev.AddMethod is { } add && Visible(add):
                        members.Add($"event {ev.Name} {Nullability(nullability.Create(ev))} {Attributes(ev.CustomAttributes)}");
                        break;
                }
            }
            lines.AddRange(members.Order(StringComparer.Ordinal).Select(m => "  " + m.TrimEnd()));
        }
        return string.Join("\n", lines.Select(l => l.TrimEnd())) + "\n";
    }

    private static string Nullability(NullabilityInfo info) =>
        $"{Name(info.Type)}[{info.ReadState}/{info.WriteState}]" +
        (info.ElementType is { } element ? $" element({Nullability(element)})" : "") +
        (info.GenericTypeArguments.Length > 0 ? " args(" + string.Join(",", info.GenericTypeArguments.Select(Nullability)) + ")" : "");

    private static string Modifiers(ParameterInfo parameter) =>
        "modreq(" + string.Join(",", parameter.GetRequiredCustomModifiers().Select(Name)) + ") " +
        "modopt(" + string.Join(",", parameter.GetOptionalCustomModifiers().Select(Name)) + ")";

    // Compiler-emitted debugger stepping metadata varies by build configuration, not API contract.
    private static string Attributes(IEnumerable<CustomAttributeData> attributes) => string.Join(" ", attributes
        .Where(a => a.AttributeType.FullName is not ("System.Runtime.CompilerServices.NullableAttribute" or
            "System.Runtime.CompilerServices.NullableContextAttribute" or "System.Runtime.CompilerServices.CompilerGeneratedAttribute" or
            "System.Diagnostics.DebuggerStepThroughAttribute"))
        .Select(a => a.ToString()).Order(StringComparer.Ordinal));

    private static string Constraints(Type[] arguments) => string.Join(" ", arguments.Where(a => a.IsGenericParameter).Select(a =>
        $"where {a.Name}: {a.GenericParameterAttributes} {string.Join(",", a.GetGenericParameterConstraints().Select(Name))} {Attributes(a.CustomAttributes)}"));

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
