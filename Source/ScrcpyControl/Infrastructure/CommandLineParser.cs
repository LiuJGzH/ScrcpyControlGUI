using System.Text;

namespace ScrcpyControl.Infrastructure;

public static class CommandLineParser
{
    public static IReadOnlyList<string> Split(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return Array.Empty<string>();
        }

        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var quote = '\0';

        for (var index = 0; index < commandLine.Length; index++)
        {
            var character = commandLine[index];
            if ((character == '"' || character == '\'') &&
                (!inQuotes || quote == character))
            {
                if (inQuotes)
                {
                    inQuotes = false;
                    quote = '\0';
                }
                else
                {
                    inQuotes = true;
                    quote = character;
                }
                continue;
            }

            if (char.IsWhiteSpace(character) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            if (character == '\\' &&
                index + 1 < commandLine.Length &&
                commandLine[index + 1] is '"' or '\'')
            {
                current.Append(commandLine[++index]);
                continue;
            }

            current.Append(character);
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }

    public static string FormatForDisplay(IEnumerable<string> arguments)
    {
        return string.Join(
            " ",
            arguments.Select(argument =>
                argument.Length == 0 ||
                argument.Any(char.IsWhiteSpace) ||
                argument.Contains('"')
                    ? "\"" + argument.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
                    : argument));
    }
}
