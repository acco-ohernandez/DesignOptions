using System.Collections.Generic;
using System.Windows;

namespace DesignOptions.Forms
{
    /// <summary>
    /// Modal report for the Pin &amp; Lock command: a table of element kinds with their
    /// pin / design-option counts, optional notes, and a next-step instruction.
    /// ShowDialog() returns true when the user clicks OK (proceed to Add to Set).
    /// </summary>
    public partial class PinLockReportWindow : Window
    {
        private const string LogTag = "[PinLockReportWindow]";

        /// <param name="headline">Bold summary line at the top.</param>
        /// <param name="subhead">Secondary line under the headline (may be null).</param>
        /// <param name="rows">Table rows, already in display order.</param>
        /// <param name="notes">Lines shown under the table (may be empty).</param>
        /// <param name="instruction">Highlighted next-step text; null hides the panel.</param>
        /// <param name="optionName">Design option name used in the two right-hand column headers.</param>
        /// <param name="canProceed">True labels the accept button OK (Add to Set follows); false labels it Apply.</param>
        public PinLockReportWindow(
            string headline,
            string subhead,
            IList<PinLockReportRow> rows,
            IList<string> notes,
            string instruction,
            string optionName,
            bool canProceed)
        {
            InitializeComponent();
            Debug.WriteLine($"{LogTag} ctor rows={rows.Count} canProceed={canProceed}");

            HeadlineText.Text = headline ?? string.Empty;

            SubheadText.Text = subhead ?? string.Empty;
            SubheadText.Visibility = string.IsNullOrWhiteSpace(subhead) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

            ToLockedColumn.Header = $"To {optionName}";
            InLockedColumn.Header = $"In {optionName}";
            ReportGrid.ItemsSource = rows;

            NotesText.Text = notes == null ? string.Empty : string.Join(Environment.NewLine, notes);
            NotesText.Visibility = string.IsNullOrWhiteSpace(NotesText.Text) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

            InstructionText.Text = instruction ?? string.Empty;
            InstructionPanel.Visibility = string.IsNullOrWhiteSpace(instruction) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

            // OK always applies the Pinned checkboxes, so both buttons stay in every mode.
            // canProceed only changes what the command does afterwards (Add to Set or not).
            OkButton.Content = canProceed ? "OK" : "Apply";
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine($"{LogTag} OK");
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine($"{LogTag} Cancel");
            DialogResult = false;
            Close();
        }
    }
}
