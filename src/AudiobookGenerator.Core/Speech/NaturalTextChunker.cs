namespace YewCone.AudiobookGenerator.Core;

internal sealed class NaturalTextChunker : ITextChunker
{
    public IReadOnlyList<string> Split(string text, int maximumCharacters)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (maximumCharacters < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        }

        if (text.Length <= maximumCharacters)
        {
            return [text];
        }

        var chunks = new List<string>();
        var start = 0;

        while (start < text.Length)
        {
            var target = Math.Min(start + maximumCharacters, text.Length);
            if (target == text.Length)
            {
                chunks.Add(text[start..]);
                break;
            }

            var minimumNaturalBoundary = start + (maximumCharacters / 2);
            var split = FindParagraphBoundary(text, start, target, minimumNaturalBoundary);
            split = split > start ? split : FindSentenceBoundary(text, start, target, minimumNaturalBoundary);
            split = split > start ? split : FindWhitespaceBoundary(text, start, target);
            split = split > start ? split : target;

            chunks.Add(text[start..split]);
            start = split;
        }

        return chunks;
    }

    private static int FindParagraphBoundary(string text, int start, int target, int minimum)
    {
        for (var index = target - 1; index >= minimum; index--)
        {
            if (text[index] == '\n' && index > start && text[index - 1] == '\n')
            {
                return index + 1;
            }
        }

        return -1;
    }

    private static int FindSentenceBoundary(string text, int start, int target, int minimum)
    {
        for (var index = target - 1; index >= minimum; index--)
        {
            if (text[index] is not ('.' or '?' or '!'))
            {
                continue;
            }

            var next = index + 1;
            if (next >= text.Length || char.IsWhiteSpace(text[next]))
            {
                return next;
            }
        }

        return -1;
    }

    private static int FindWhitespaceBoundary(string text, int start, int target)
    {
        for (var index = target - 1; index > start; index--)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                return index + 1;
            }
        }

        return -1;
    }
}
