using System.Text;
using System.IO;

namespace SqlObserver.Tools;

public static class SqlPolicyTokens
{
    public static string WithoutQuotedTextAndComments(string source)
    {
        var result = new StringBuilder(source.Length);
        int blockDepth = 0;
        char quote = '\0';
        bool lineComment = false;
        for (int i = 0; i < source.Length; i++)
        {
            char current = source[i], next = i + 1 < source.Length ? source[i + 1] : '\0';
            if (lineComment)
            {
                if (current is '\r' or '\n') lineComment = false;
                result.Append(current is '\r' or '\n' ? current : ' ');
            }
            else if (blockDepth > 0)
            {
                if (current == '/' && next == '*') { blockDepth++; i++; }
                else if (current == '*' && next == '/') { blockDepth--; i++; }
                result.Append(' ');
            }
            else if (quote != '\0')
            {
                if (current == quote)
                {
                    if (next == quote) i++; else quote = '\0';
                }
                result.Append(' ');
            }
            else if (current == '-' && next == '-') { lineComment = true; i++; result.Append(' '); }
            else if (current == '/' && next == '*') { blockDepth = 1; i++; result.Append(' '); }
            else if (current is '\'' or '"' or '[') { quote = current == '[' ? ']' : current; result.Append(' '); }
            else result.Append(current);
        }
        if (quote != '\0' || blockDepth != 0) throw new InvalidDataException("Unterminated SQL quote or comment.");
        return result.ToString();
    }
}
