using System.Collections.Generic;
using System.Linq;
using DesignOptions.Common;
using DesignOptions.Forms;

namespace DesignOptions
{
    /// <summary>
    /// Copies the project standards listed in Transfer_Project_Standards.txt from another
    /// open model into the active one, overwriting standards that already exist with the
    /// same name. The source defaults to the other open project; a checklist window lets the
    /// user change it and untick standards before running, then shows what happened.
    ///
    /// Line Weights have no API at all and non-shared project parameters cannot be created
    /// through the API; both are reported rather than attempted.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class Cmd_TransferProjectStandards : IExternalCommand
    {
        private const string LogTag = "[TransferProjectStandards]";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                Debug.WriteLine($"{LogTag} Execute start, target=\"{doc.Title}\"");

                if (doc.IsFamilyDocument)
                {
                    TaskDialog.Show("Transfer Project Standards", "This command only works in a project document.");
                    return Result.Cancelled;
                }

                // Source candidates: every other open, non-linked project in this Revit session.
                List<Document> sources = uiapp.Application.Documents
                    .Cast<Document>()
                    .Where(d => !d.IsFamilyDocument && !d.IsLinked && !d.Equals(doc))
                    .ToList();
                Debug.WriteLine($"{LogTag} source candidates={sources.Count}: {string.Join(", ", sources.Select(s => s.Title))}");

                if (sources.Count == 0)
                {
                    TaskDialog td = new TaskDialog("Transfer Project Standards")
                    {
                        MainInstruction = "No other project is open.",
                        MainContent =
                            "Open the model you want to copy standards FROM in this Revit session, " +
                            $"make \"{doc.Title}\" the active model, then run this command again."
                    };
                    td.Show();
                    return Result.Cancelled;
                }

                List<TransferStandardRow> rows = CreateRows();

                // Revit raises informational message boxes while print settings are read or
                // copied (printer / paper size mismatches). Answer them automatically during the
                // run and surface the text in the window instead.
                TransferStandardsWindow window = null;
                window = new TransferStandardsWindow(
                    doc, sources, rows,
                    (source, allRows) =>
                    {
                        using (RevitDialogSuppressor quiet = new RevitDialogSuppressor(uiapp))
                        {
                            RunTransfer(source, doc, allRows);
                            foreach (string msg in quiet.Messages) window?.AddNote("Revit message (auto-closed): " + msg);
                        }
                    },
                    (source, label) =>
                    {
                        using (RevitDialogSuppressor quiet = new RevitDialogSuppressor(uiapp))
                        {
                            string path = ProjectStandardsSnapshot.Write(new[] { source, doc }, doc, label);
                            foreach (string msg in quiet.Messages) window?.AddNote("Revit message (auto-closed): " + msg);
                            return path;
                        }
                    });
                new System.Windows.Interop.WindowInteropHelper(window) { Owner = uiapp.MainWindowHandle };
                bool ran = window.ShowDialog() == true;

                Debug.WriteLine($"{LogTag} Execute end, ran={ran}");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"{LogTag} EXCEPTION: {ex}");
                message = ex.Message;
                return Result.Failed;
            }
        }

        /// <summary>
        /// The standards in Transfer_Project_Standards.txt, in that order.
        /// </summary>
        private static List<TransferStandardRow> CreateRows()
        {
            return new List<TransferStandardRow>
            {
                Row(ProjectStandardKind.ArrowheadStyles, "Arrowhead Styles"),
                Row(ProjectStandardKind.BrowserOrganization, "Browser Organization"),
                Row(ProjectStandardKind.DimensionStyles, "Dimension Styles"),
                Row(ProjectStandardKind.DwgExportSetups, "DWG/DXF Export Setup Settings"),
                Row(ProjectStandardKind.ElevationTagTypes, "Elevation Tag Types"),
                Row(ProjectStandardKind.GridTypes, "Grid Types"),
                Row(ProjectStandardKind.LevelTypes, "Level Types"),
                Row(ProjectStandardKind.LineStyles, "Line Styles"),
                Row(ProjectStandardKind.LineWeights, "Line Weights",
                    "The Revit API has no access to the line weights table. Set them by hand under Manage > Additional Settings > Line Weights."),
                Row(ProjectStandardKind.ModelTextTypes, "Model Text Types"),
                Row(ProjectStandardKind.ObjectStyles, "Object Styles"),
                Row(ProjectStandardKind.PhaseSettings, "Phase Settings"),
                Row(ProjectStandardKind.PrintSettings, "Print Settings"),
                Row(ProjectStandardKind.ProjectParameters, "Project Parameters"),
                Row(ProjectStandardKind.RevisionSettings, "Revision Settings"),
                Row(ProjectStandardKind.SpotDimensionSettings, "Spot Dimension Settings"),
                Row(ProjectStandardKind.TextTypes, "Text Types"),
                Row(ProjectStandardKind.ViewReferenceTypes, "View Reference Types"),
                Row(ProjectStandardKind.ViewportTypes, "Viewport Types")
            };
        }

        private static TransferStandardRow Row(ProjectStandardKind kind, string name, string unsupportedReason = null)
        {
            return new TransferStandardRow
            {
                Kind = kind,
                Name = name,
                IsSupported = unsupportedReason == null,
                UnsupportedReason = unsupportedReason
            };
        }

        /// <summary>
        /// Runs every selected standard in its own transaction inside one transaction group,
        /// so a failure in one standard rolls back only that standard and the whole run is a
        /// single Undo step. Fills in each row's Status / New / Updated / Notes.
        /// </summary>
        private static void RunTransfer(Document source, Document target, IList<TransferStandardRow> rows)
        {
            Debug.WriteLine($"{LogTag} RunTransfer source=\"{source.Title}\" target=\"{target.Title}\"");

            using (TransactionGroup group = new TransactionGroup(target, "Transfer Project Standards"))
            {
                group.Start();
                ProjectStandardsTransfer engine = new ProjectStandardsTransfer(source, target);

                foreach (TransferStandardRow row in rows)
                {
                    if (!row.IsSupported)
                    {
                        row.Status = "Not supported";
                        row.Notes = row.UnsupportedReason ?? string.Empty;
                        continue;
                    }
                    if (!row.Selected)
                    {
                        row.Status = "Skipped";
                        continue;
                    }

                    using (Transaction t = new Transaction(target, $"Transfer {row.Name}"))
                    {
                        TransferFailuresPreprocessor failures = new TransferFailuresPreprocessor();
                        t.Start();
                        t.SetFailureHandlingOptions(t.GetFailureHandlingOptions()
                            .SetFailuresPreprocessor(failures)
                            .SetClearAfterRollback(true));
                        try
                        {
                            TransferOutcome outcome = engine.Run(row.Kind);
                            TransactionStatus status = t.Commit();

                            if (status == TransactionStatus.Committed)
                            {
                                row.Created = outcome.Created;
                                row.Updated = outcome.Updated;
                                row.Skipped = outcome.Skipped;
                                row.Notes = string.Join(" ", outcome.Notes);
                                row.Status = outcome.Skipped > 0 ? "Partial"
                                    : outcome.Created + outcome.Updated > 0 ? "Done"
                                    : "Nothing to do";
                            }
                            else
                            {
                                row.Status = "Failed";
                                row.Notes = "Revit rolled the change back: " + string.Join(" ", failures.Errors);
                            }
                            Debug.WriteLine($"{LogTag} {row.Name}: {row.Status} new={row.Created} updated={row.Updated} skipped={row.Skipped}");
                        }
                        catch (Exception ex)
                        {
                            if (t.GetStatus() == TransactionStatus.Started) t.RollBack();
                            row.Status = "Failed";
                            row.Notes = ex.Message;
                            Debug.WriteLine($"{LogTag} {row.Name} FAILED: {ex}");
                        }
                    }
                }

                group.Assimilate();
            }
        }

        internal static PushButtonData GetButtonData()
        {
            string buttonInternalName = "btnTransferProjectStandards";
            string buttonTitle = "Transfer Standards";

            Common.ButtonDataClass myButtonData = new Common.ButtonDataClass(
                buttonInternalName,
                buttonTitle,
                MethodBase.GetCurrentMethod().DeclaringType?.FullName,
                Properties.Resources.TransferStandards_32x32,
                Properties.Resources.TransferStandards_16x16,
                Properties.Resources.TransferStandards_Dark_32x32,
                Properties.Resources.TransferStandards_Dark_16x16,
                "Copies ACCO project standards (arrowheads, text and dimension types, line and object styles, " +
                "phases, print and export setups, shared project parameters...) from another open model into this one, " +
                "overwriting standards with the same name.");

            return myButtonData.Data;
        }
    }
}
