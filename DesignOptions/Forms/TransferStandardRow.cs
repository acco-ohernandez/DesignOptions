using System.ComponentModel;
using System.Runtime.CompilerServices;
using DesignOptions.Common;

namespace DesignOptions.Forms
{
    /// <summary>
    /// One row of the Transfer Project Standards window: a standard, whether the user
    /// wants it, and (after the run) what happened to it. Raises PropertyChanged so the
    /// grid refreshes while the command fills in the results.
    /// </summary>
    public class TransferStandardRow : INotifyPropertyChanged
    {
        internal ProjectStandardKind Kind { get; set; }

        public string Name { get; set; }

        /// <summary>False for standards the Revit API cannot transfer (reported, never run).</summary>
        public bool IsSupported { get; set; } = true;

        /// <summary>Shown in the Notes column for unsupported standards.</summary>
        public string UnsupportedReason { get; set; }

        private bool _selected = true;
        /// <summary>Bound to the "Transfer" checkbox.</summary>
        public bool Selected
        {
            get => _selected;
            set { if (_selected != value) { _selected = value; OnPropertyChanged(); } }
        }

        private bool _hasRun;
        /// <summary>Set by the window once the transfer has run; locks the checkbox.</summary>
        public bool HasRun
        {
            get => _hasRun;
            set
            {
                if (_hasRun == value) return;
                _hasRun = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanToggle));
                OnPropertyChanged(nameof(NewText));
                OnPropertyChanged(nameof(UpdatedText));
            }
        }

        public bool CanToggle => IsSupported && !HasRun;

        private string _status = string.Empty;
        public string Status
        {
            get => _status;
            set { if (_status != value) { _status = value ?? string.Empty; OnPropertyChanged(); } }
        }

        private int _created;
        public int Created
        {
            get => _created;
            set { if (_created != value) { _created = value; OnPropertyChanged(); OnPropertyChanged(nameof(NewText)); } }
        }

        private int _updated;
        public int Updated
        {
            get => _updated;
            set { if (_updated != value) { _updated = value; OnPropertyChanged(); OnPropertyChanged(nameof(UpdatedText)); } }
        }

        private int _skipped;
        public int Skipped
        {
            get => _skipped;
            set { if (_skipped != value) { _skipped = value; OnPropertyChanged(); } }
        }

        /// <summary>Blank until the transfer has run, so the grid does not show misleading zeros.</summary>
        public string NewText => HasRun && IsSupported && Selected ? Created.ToString() : string.Empty;
        public string UpdatedText => HasRun && IsSupported && Selected ? Updated.ToString() : string.Empty;

        private string _notes = string.Empty;
        public string Notes
        {
            get => _notes;
            set { if (_notes != value) { _notes = value ?? string.Empty; OnPropertyChanged(); } }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
