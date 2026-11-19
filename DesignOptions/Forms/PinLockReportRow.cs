using System.Collections.Generic;

namespace DesignOptions.Forms
{
    /// <summary>
    /// One row of the Pin &amp; Lock report: a kind of element and its counts.
    /// ToLocked / InLocked are strings so kinds that cannot join a design option
    /// (levels) can show "n/a" instead of a misleading zero.
    /// </summary>
    public class PinLockReportRow
    {
        public string Kind { get; set; }
        public int Found { get; set; }
        public int PinnedNow { get; set; }
        public int AlreadyPinned { get; set; }
        public string ToLocked { get; set; }
        public string InLocked { get; set; }
        public bool IsTotal { get; set; }

        /// <summary>
        /// Bound to the "Pinned" checkbox. Starts true (the command has just pinned
        /// everything); the user unchecks it to unpin this kind when clicking OK.
        /// </summary>
        public bool Pin { get; set; } = true;

        /// <summary>The elements behind this row, so the command can act on the checkbox.</summary>
        public List<Element> Elements { get; set; } = new List<Element>();

        /// <summary>The Total row has no checkbox.</summary>
        public bool ShowCheckBox => !IsTotal;
    }
}
