using System.Collections.Generic;
using Autodesk.Revit.UI.Events;

namespace DesignOptions.Common
{
    /// <summary>
    /// While alive, answers Revit's informational message boxes automatically (for example
    /// "PAZS101 cannot be used with 18x24 Spools print settings") so a transfer or snapshot
    /// runs without stopping. Every suppressed message is kept in <see cref="Messages"/> so
    /// the command can still show it to the user afterwards.
    /// </summary>
    internal sealed class RevitDialogSuppressor : IDisposable
    {
        private const string LogTag = "[DialogSuppressor]";
        private readonly UIApplication _uiapp;

        public List<string> Messages { get; } = new List<string>();

        public RevitDialogSuppressor(UIApplication uiapp)
        {
            _uiapp = uiapp;
            if (_uiapp != null) _uiapp.DialogBoxShowing += OnDialogBoxShowing;
        }

        private void OnDialogBoxShowing(object sender, DialogBoxShowingEventArgs e)
        {
            string text = null;
            if (e is MessageBoxShowingEventArgs mb) text = mb.Message;
            else if (e is TaskDialogShowingEventArgs td) text = td.Message;
            if (string.IsNullOrWhiteSpace(text)) text = e.DialogId;

            Debug.WriteLine($"{LogTag} suppressed dialog {e.DialogId}: {text}");
            if (!Messages.Contains(text)) Messages.Add(text);

            // 1 = IDOK, which also dismisses a single-button "Close" message box.
            try { e.OverrideResult(1); }
            catch (Exception ex) { Debug.WriteLine($"{LogTag} could not override dialog: {ex.Message}"); }
        }

        public void Dispose()
        {
            if (_uiapp != null) _uiapp.DialogBoxShowing -= OnDialogBoxShowing;
        }
    }
}
