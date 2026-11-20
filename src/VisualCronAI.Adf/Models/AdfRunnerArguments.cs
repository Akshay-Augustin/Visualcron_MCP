using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VisualCronAI.Adf.Models;

/// <summary>
/// The command line that a VisualCron "Execute" task passes to VisualCronAI.AdfRunner.exe.
/// Built by the MCP server, parsed by the runner (and by the MCP server when reading jobs back).
/// Contains only non-secret values: environment name, pipeline name and pipeline parameters.
/// </summary>
public sealed partial class AdfRunnerArguments
{
    public const string RunVerb = "run";
    public const int DefaultTimeoutMinutes = 120;
    public const int DefaultPollSeconds = 30;

    public string Environment { get; set; } = "";
    public string PipelineName { get; set; } = "";
    /// <summary>Pipeline parameters. Keys may carry a type suffix: name:int, name:bool, name:float, name:json (default string).</summary>
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.Ordinal);
    public int TimeoutMinutes { get; set; } = DefaultTimeoutMinutes;
    public int PollSeconds { get; set; } = DefaultPollSeconds;
    public bool CancelOnTimeout { get; set; } = true;

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_\-]{0,31}$")]
    private static partial Regex EnvRegex();
    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_\-\. ]{0,259}$")]
    private static partial Regex PipelineRegex();
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,127}(:(string|int|bool|float|json))?$")]
    private static partial Regex ParamNameRegex();

    public static readonly string[] ParameterTypes = ["string", "int", "bool", "float", "json"];

    /// <summary>Returns validation errors (empty when valid).</summary>
    public List<string> Validate()
    {
        var errors = new List<string>();
        if (!EnvRegex().IsMatch(Environment)) errors.Add($"ADF environment '{Environment}' is invalid (letters, digits, '_' or '-', max 32).");
        if (!PipelineRegex().IsMatch(PipelineName)) errors.Add($"Pipeline name '{PipelineName}' is invalid.");
        if (TimeoutMinutes is < 1 or > 24 * 60) errors.Add("TimeoutMinutes must be between 1 and 1440.");
        if (PollSeconds is < 5 or > 600) errors.Add("PollSeconds must be between 5 and 600.");
        if (Parameters.Count > 100) errors.Add("At most 100 pipeline parameters are allowed.");
        foreach (var (key, value) in Parameters)
        {
            if (!ParamNameRegex().IsMatch(key)) errors.Add($"Parameter name '{key}' is invalid.");
            if (value.Length > 4000) errors.Add($"Parameter '{key}' value is too long (max 4000).");
            if (value.Any(c => c == '"' || char.IsControl(c))) errors.Add($"Parameter '{key}' value must not contain double quotes or control characters.");
            string type = ParameterType(key);
            if (type == "int" && !long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) && !IsVisualCronVariable(value))
                errors.Add($"Parameter '{key}' is declared int but value '{value}' is not an integer.");
            if (type == "bool" && !bool.TryParse(value, out _) && !IsVisualCronVariable(value))
                errors.Add($"Parameter '{key}' is declared bool but value '{value}' is not true/false.");
        }
        return errors;
    }

    public static string ParameterType(string key)
    {
        int i = key.IndexOf(':');
        return i < 0 ? "string" : key[(i + 1)..];
    }

    public static string ParameterName(string key)
    {
        int i = key.IndexOf(':');
        return i < 0 ? key : key[..i];
    }

    /// <summary>VisualCron expands {VARIABLE} expressions in task arguments at run time.</summary>
    public static bool IsVisualCronVariable(string value) => value.StartsWith('{') && value.EndsWith('}');

    public string ToCommandLine()
    {
        var args = new List<string> { RunVerb, "--env", Environment, "--pipeline", PipelineName };
        foreach (var (key, value) in Parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            args.Add("--param");
            args.Add(key + "=" + value);
        }
        if (TimeoutMinutes != DefaultTimeoutMinutes) { args.Add("--timeout-minutes"); args.Add(TimeoutMinutes.ToString(CultureInfo.InvariantCulture)); }
        if (PollSeconds != DefaultPollSeconds) { args.Add("--poll-seconds"); args.Add(PollSeconds.ToString(CultureInfo.InvariantCulture)); }
        if (!CancelOnTimeout) args.Add("--no-cancel-on-timeout");
        return string.Join(" ", args.Select(CommandLine.Quote));
    }

    public static AdfRunnerArguments Parse(IReadOnlyList<string> argv)
    {
        if (argv.Count == 0 || !string.Equals(argv[0], RunVerb, StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Expected the 'run' verb.");
        var result = new AdfRunnerArguments();
        for (int i = 1; i < argv.Count; i++)
        {
            string a = argv[i];
            string Next() => i + 1 < argv.Count ? argv[++i] : throw new FormatException($"Missing value for {a}.");
            switch (a.ToLowerInvariant())
            {
                case "--env": result.Environment = Next(); break;
                case "--pipeline": result.PipelineName = Next(); break;
                case "--param":
                {
                    string kv = Next();
                    int eq = kv.IndexOf('=');
                    if (eq <= 0) throw new FormatException($"--param expects name=value, got '{kv}'.");
                    result.Parameters[kv[..eq]] = kv[(eq + 1)..];
                    break;
                }
                case "--timeout-minutes": result.TimeoutMinutes = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--poll-seconds": result.PollSeconds = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--no-cancel-on-timeout": result.CancelOnTimeout = false; break;
                default: throw new FormatException($"Unknown argument '{a}'.");
            }
        }
        return result;
    }

    public static AdfRunnerArguments Parse(string commandLine) => Parse(CommandLine.Split(commandLine));

    /// <summary>True when the Execute task's command points at the ADF runner.</summary>
    public static bool IsRunnerCommand(string cmdLine) =>
        cmdLine.TrimEnd('"').EndsWith("VisualCronAI.AdfRunner.exe", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Windows command-line quoting/splitting compatible with CommandLineToArgvW and .NET's argv parsing.</summary>
public static class CommandLine
{
    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0) return arg;
        var sb = new StringBuilder("\"");
        for (int i = 0; i < arg.Length; i++)
        {
            int backslashes = 0;
            while (i < arg.Length && arg[i] == '\\') { backslashes++; i++; }
            if (i == arg.Length) { sb.Append('\\', backslashes * 2); break; }
            if (arg[i] == '"') { sb.Append('\\', backslashes * 2 + 1).Append('"'); }
            else { sb.Append('\\', backslashes).Append(arg[i]); }
        }
        return sb.Append('"').ToString();
    }

    public static List<string> Split(string commandLine)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false, hasToken = false;
        for (int i = 0; i < commandLine.Length; i++)
        {
            char c = commandLine[i];
            if (c == '\\')
            {
                int n = 0;
                while (i < commandLine.Length && commandLine[i] == '\\') { n++; i++; }
                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    current.Append('\\', n / 2);
                    if (n % 2 == 1) current.Append('"');
                    else inQuotes = !inQuotes;
                }
                else
                {
                    current.Append('\\', n);
                    i--;
                }
                hasToken = true;
            }
            else if (c == '"')
            {
                if (inQuotes && i + 1 < commandLine.Length && commandLine[i + 1] == '"') { current.Append('"'); i++; }
                else inQuotes = !inQuotes;
                hasToken = true;
            }
            else if (!inQuotes && (c == ' ' || c == '\t'))
            {
                if (hasToken) { result.Add(current.ToString()); current.Clear(); hasToken = false; }
            }
            else
            {
                current.Append(c);
                hasToken = true;
            }
        }
        if (hasToken) result.Add(current.ToString());
        return result;
    }
}
