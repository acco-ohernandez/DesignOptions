using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace DesignOptions.Forms
{
    /// <summary>
    /// Modal checklist for the Transfer Project Standards command. The user picks the
    /// source model and the standards to copy, clicks Transfer, and the same grid then
    /// shows the per-standard result. ShowDialog() returns true once a transfer has run.
    /// </summary>
    public partial class TransferStandardsWindow : Window
    {
        private const string LogTag = "[TransferStandardsWindow]";

        private readonly IList<TransferStandardRow> _rows;
        private readonly Action<Document, IList<TransferStandardRow>> _runTransfer;
        private bool _hasRun;

        /// <param name="target">The document that receives the standards (the active one).</param>
        /// <param name="sources">Open project documents the user may copy from; the first is preselected.</param>
        /// <param name="rows">One row per standard, in display order.</param>
        /// <param name="runTransfer">Called on Transfer with the chosen source and all rows; fills in each row's result.</param>
        public TransferStandardsWindow(
            Document target,
            IList<Document> sources,
            IList<TransferStandardRow> rows,
            Action<Document, IList<TransferStandardRow>> runTransfer)
        {
            InitializeComponent();
            _rows = rows;
            _runTransfer = runTransfer;
            Debug.WriteLine($"{LogTag} ctor sources={sources.Count} rows={rows.Count}");

            SourceCombo.ItemsSource = sources;
            SourceCombo.SelectedIndex = sources.Count > 0 ? 0 : -1;
            TargetText.Text = target.Title;

            HeadlineText.Text = "Transfer project standards";
            SubheadText.Text = "Standards with the same name in the target model are overwritten. " +
                               "Uncheck anything you do not want copied, then click Transfer.";
            ReportGrid.ItemsSource = rows;

            foreach (TransferStandardRow row in rows.Where(r => !r.IsSupported))
            {
                row.Selected = false;
                row.Status = "Not supported";
                row.Notes = row.UnsupportedReason ?? string.Empty;
            }

            NotesText.Text = string.Empty;
            NotesText.Visibility = System.Windows.Visibility.Collapsed;

            TransferButton.IsEnabled = sources.Count > 0;

            // Keep the "n of m selected" label live as checkboxes change.
            foreach (TransferStandardRow row in rows)
            {
                row.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(TransferStandardRow.Selected)) UpdateSelectionCount();
                };
            }
            UpdateSelectionCount();
        }

        private void UpdateSelectionCount()
        {
            int available = _rows.Count(r => r.IsSupported);
            int selected = _rows.Count(r => r.IsSupported && r.Selected);
            SelectionCountText.Text = $"{selected} of {available} selected";
            Debug.WriteLine($"{LogTag} selection {selected}/{available}");
        }

        private void CheckAllButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (TransferStandardRow row in _rows.Where(r => r.CanToggle)) row.Selected = true;
        }

        private void UncheckAllButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (TransferStandardRow row in _rows.Where(r => r.CanToggle)) row.Selected = false;
        }

        private void TransferButton_Click(object sender, RoutedEventArgs e)
        {
            Document source = SourceCombo.SelectedItem as Document;
            if (source == null)
            {
                SubheadText.Text = "Choose the model to copy from.";
                return;
            }

            int selected = _rows.Count(r => r.Selected && r.IsSupported);
            if (selected == 0)
            {
                SubheadText.Text = "Check at least one standard to transfer.";
                return;
            }

            Debug.WriteLine($"{LogTag} Transfer clicked: source=\"{source.Title}\" selected={selected}");

            HeadlineText.Text = "Transferring...";
            SubheadText.Text = string.Empty;
            TransferButton.IsEnabled = false;
            CheckAllButton.IsEnabled = false;
            UncheckAllButton.IsEnabled = false;
            SourceCombo.IsEnabled = false;

            Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            try
            {
                _runTransfer(source, _rows);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"{LogTag} EXCEPTION during transfer: {ex}");
                NotesText.Text = $"The transfer stopped with an error: {ex.Message}";
                NotesText.Visibility = System.Windows.Visibility.Visible;
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }

            _hasRun = true;
            foreach (TransferStandardRow row in _rows) row.HasRun = true;

            int ran = _rows.Count(r => r.Selected && r.IsSupported);
            int failed = _rows.Count(r => r.Status == "Failed");
            int created = _rows.Where(r => r.Selected && r.IsSupported).Sum(r => r.Created);
            int updated = _rows.Where(r => r.Selected && r.IsSupported).Sum(r => r.Updated);

            HeadlineText.Text = failed == 0
                ? $"Done: {created} new, {updated} updated across {ran} standard(s)"
                : $"Done with {failed} failure(s): {created} new, {updated} updated";
            SubheadText.Text = $"Copied from \"{source.Title}\". Review the Notes column for anything that needs a manual step. " +
                               "Use Undo in Revit to revert the whole transfer.";

            TransferButton.Visibility = System.Windows.Visibility.Collapsed;
            CancelButton.Content = "Close";
            Debug.WriteLine($"{LogTag} transfer finished: created={created} updated={updated} failed={failed}");
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine($"{LogTag} {(_hasRun ? "Close" : "Cancel")}");
            DialogResult = _hasRun;
            Close();
        }
    }
}
