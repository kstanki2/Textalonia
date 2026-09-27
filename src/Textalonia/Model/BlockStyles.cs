namespace Textalonia.Model;

/// <summary>Independent device-independent pixel insets. Null on a container preserves its legacy padding.</summary>
public sealed record EdgeInsets(double Left = 0, double Top = 0, double Right = 0, double Bottom = 0);

/// <summary>An inset solid border. A null color inherits the editor's border color.</summary>
public sealed record BorderSide(double Width = 0, string? Color = null);

/// <summary>Independent borders. Missing sides have no border; a null container value preserves legacy borders.</summary>
public sealed record BlockBorders(BorderSide? Left = null, BorderSide? Top = null, BorderSide? Right = null, BorderSide? Bottom = null);
