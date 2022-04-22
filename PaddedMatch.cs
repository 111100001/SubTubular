using System;
using System.Collections.Generic;
using System.Linq;

namespace SubTubular
{
    /// <summary>
    /// A helper comparable to <see cref="Match"/> but containing a match
    /// padded with a number of characters on each end for context.
    /// </summary>
    internal sealed class PaddedMatch : Range<int>
    {
        internal string Value { get; }

        private PaddedMatch(int start, int end, string fullText)
            : base(start, end, endIncluded: true)
            // add 1 to calculate Length because both Start and End are included
            => Value = fullText.Substring(Start, End - Start + 1);

        /// <summary>Used for <paramref name="padding"/> a match with
        /// <paramref name="start"/> relative to <paramref name="fullText"/>.</summary>
        internal PaddedMatch(int start, int length, ushort padding, string fullText)
            : this(start: GetPaddedStartIndex(start, padding),
                end: GetPaddedEndIndex(start, length, padding, fullText),
                fullText: fullText)
        {
        }

        /// <summary>Used for merging <paramref name="overlapping"/> padded matches
        /// into one spanning all of them to avoid repetition in the output.</summary>
        internal PaddedMatch(IEnumerable<PaddedMatch> overlapping, string fullText)
            : this(overlapping.Min(m => m.Start), overlapping.Max(m => m.End), fullText) { }

        public override int GetHashCode() => HashCode.Combine(base.GetHashCode(), Value);
        public override bool Equals(object obj) => obj == null ? false : obj.GetHashCode() == GetHashCode();

        // starting at zero or index minus padding
        private static int GetPaddedStartIndex(int start, ushort padding)
            => start <= padding ? 0 : start - (int)padding;

        // ending at last index of fullText or last match index plus padding
        private static int GetPaddedEndIndex(int start, int length, ushort padding, string fullText)
        {
            // substract 1 because start is included in length
            var paddedEnd = start + length - 1 + (int)padding;
            var lastIndex = fullText.Length - 1;
            return paddedEnd > lastIndex ? lastIndex : paddedEnd;
        }
    }

    /// <summary>Extensions for <see cref="PaddedMatch"/>.</summary>
    internal static class MatchExtenions
    {
        /// <summary>Merges overlapping <paramref name="orTouching"/> <paramref name="matches"/> together using
        /// <paramref name="fullText"/> to facilitate selecting the <see cref="PaddedMatch.Value"/> of the merged match.</summary>
        internal static IEnumerable<PaddedMatch> MergeOverlapping(this IEnumerable<PaddedMatch> matches, string fullText, bool orTouching = true)
            => matches.GroupOverlapping(orTouching).Select(group => group.Count() == 1 ? group.First() : new PaddedMatch(group, fullText));
    }
}