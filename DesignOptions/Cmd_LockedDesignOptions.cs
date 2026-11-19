using System.Collections.Generic;
using System.Linq;
using DesignOptions.Forms;

namespace DesignOptions
{
    /// <summary>
    /// Collects every Revit link, linked CAD file (DWG etc.), grid and scope box
    /// in the main model, pins them, then hands them to Revit's "Add to Set"
    /// command so they can be placed in the ACCO > LOCKED design option.
    ///
    /// NOTE: The Revit API cannot create design options or assign elements to
    /// one (Element.DesignOption / DESIGN_OPTION_ID are read-only in every
    /// version through 2026). The last step therefore posts the built-in
    /// AddToSet command, which opens the "Add to Design Option Set" dialog
    /// with the elements already selected. The user picks ACCO > LOCKED there.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class Cmd_LockedDesignOptions : IExternalCommand
    {
        private const string OptionSetName = "ACCO";
        private const string OptionName = "LOCKED";
        private const string LogTag = "[LockedDesignOptions]";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                Debug.WriteLine($"{LogTag} Execute start");

                if (doc.IsFamilyDocument)
                {
                    TaskDialog.Show("Locked Design Options", "This command only works in a project document.");
                    return Result.Cancelled;
                }

                // 1. Make sure the target design option exists. The API cannot create it.
                DesignOption lockedOption = FindDesignOption(doc, OptionSetName, OptionName);
                if (lockedOption == null)
                {
                    TaskDialog td = new TaskDialog("Locked Design Options")
                    {
                        MainInstruction = $"Design option \"{OptionSetName} > {OptionName}\" was not found.",
                        MainContent =
                            "The Revit API cannot create design options, so please create it first:\n\n" +
                            $"Manage tab > Design Options > New set named \"{OptionSetName}\" " +
                            $"and rename its option to \"{OptionName}\".\n\n" +
                            "Then run this command again."
                    };
                    td.Show();
                    return Result.Cancelled;
                }

                // 2. Gather the candidate elements that are still in the main model.
                List<Element> candidates = CollectCandidates(doc);
                List<Element> inMainModel = candidates.Where(e => e.DesignOption == null).ToList();
                List<Element> alreadyInLocked = candidates
                    .Where(e => e.DesignOption != null && e.DesignOption.Id == lockedOption.Id)
                    .ToList();
                int inOtherOption = candidates.Count - inMainModel.Count - alreadyInLocked.Count;

                Debug.WriteLine($"{LogTag} candidates={candidates.Count} mainModel={inMainModel.Count} " +
                                $"alreadyLocked={alreadyInLocked.Count} otherOption={inOtherOption}");

                // 3. Pin everything: the new main-model elements, anything already in LOCKED
                //    that someone has since unpinned, and all levels. Levels are pinned only:
                //    Revit does not allow levels inside a design option, so they stay out of
                //    the selection.
                List<Level> levels = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .ToList();
                Debug.WriteLine($"{LogTag} levels={levels.Count}");

                List<Element> allHandled = inMainModel.Concat(alreadyInLocked).Concat(levels).ToList();
                HashSet<ElementId> pinnedNow = PinElements(doc, allHandled);

                List<PinLockReportRow> rows = BuildReportRows(
                    allHandled,
                    pinnedNow,
                    new HashSet<ElementId>(inMainModel.Select(e => e.Id)),
                    new HashSet<ElementId>(alreadyInLocked.Select(e => e.Id)));

                List<string> notes = new List<string>();
                if (levels.Count > 0)
                    notes.Add("Levels are pinned only. Revit does not allow levels in a design option.");
                if (inOtherOption > 0)
                    notes.Add($"Skipped {inOtherOption} element(s) that sit in a different design option.");

                bool canProceed = inMainModel.Count > 0;

                string headline = canProceed
                    ? $"{pinnedNow.Count} element(s) pinned, {inMainModel.Count} selected for {OptionSetName} > {OptionName}"
                    : $"Nothing new to add to {OptionSetName} > {OptionName}";
                string subhead = canProceed
                    ? "Uncheck \"Pinned\" on a row to unpin those elements when you click OK."
                    : $"{pinnedNow.Count} element(s) re-pinned. Uncheck \"Pinned\" on a row to unpin those elements when you click Apply.";
                string instruction = canProceed
                    ? $"Click OK to open Revit's \"Add to Design Option Set\" dialog, then choose set \"{OptionSetName}\" " +
                      $"and option \"{OptionName}\" to finish. (The Revit API cannot assign elements to a design option directly.)"
                    : null;

                if (canProceed)
                {
                    // Select now so Revit's Add to Set command (posted after OK) acts on these elements.
                    uidoc.Selection.SetElementIds(inMainModel.Select(e => e.Id).ToList());
                }

                PinLockReportWindow window = new PinLockReportWindow(
                    headline, subhead, rows, notes, instruction, OptionName, canProceed);
                new System.Windows.Interop.WindowInteropHelper(window) { Owner = uiapp.MainWindowHandle };
                bool proceed = window.ShowDialog() == true;

                if (!proceed)
                {
                    Debug.WriteLine($"{LogTag} user cancelled; pins left as applied, no AddToSet");
                    return Result.Succeeded;
                }

                // 4. Honour the "Pinned" checkboxes: any row the user unchecked gets unpinned.
                List<Element> toUnpin = rows
                    .Where(r => !r.IsTotal && !r.Pin)
                    .SelectMany(r => r.Elements)
                    .ToList();
                if (toUnpin.Count > 0)
                {
                    int unpinned = UnpinElements(doc, toUnpin);
                    Debug.WriteLine($"{LogTag} user unchecked {toUnpin.Count} element(s); unpinned {unpinned}");
                }

                if (!canProceed)
                    return Result.Succeeded;

                // 5. Post Revit's "Add to Set" command, which opens the Add to Design
                //    Option Set dialog for the current selection.

                RevitCommandId addToSet = RevitCommandId.LookupPostableCommandId(PostableCommand.AddToSet);
                if (addToSet != null && uiapp.CanPostCommand(addToSet))
                {
                    uiapp.PostCommand(addToSet);
                    Debug.WriteLine($"{LogTag} posted AddToSet");
                }
                else
                {
                    TaskDialog.Show("Locked Design Options",
                        "Could not launch Revit's \"Add to Set\" command automatically.\n\n" +
                        "The elements are still selected: use Manage tab > Design Options > Add to Set " +
                        $"and choose {OptionSetName} > {OptionName}.");
                }

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
        /// Revit link instances, linked CAD import instances (DWG, DXF, DGN...), grids, scope boxes.
        /// </summary>
        private static List<Element> CollectCandidates(Document doc)
        {
            List<Element> result = new List<Element>();

            List<RevitLinkInstance> revitLinkInstances = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .ToList();
            result.AddRange(revitLinkInstances);

            List<ImportInstance> linkedCadInstances = new FilteredElementCollector(doc)
                .OfClass(typeof(ImportInstance))
                .Cast<ImportInstance>()
                .Where(ii => ii.IsLinked && !ii.ViewSpecific)
                .ToList();
            result.AddRange(linkedCadInstances);

            List<Grid> grids = new FilteredElementCollector(doc)
                .OfClass(typeof(Grid))
                .Cast<Grid>()
                .ToList();
            result.AddRange(grids);

            List<Element> scopeBoxes = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_VolumeOfInterest)
                .WhereElementIsNotElementType()
                .ToList();
            result.AddRange(scopeBoxes);

            Debug.WriteLine($"{LogTag} revitLinks={revitLinkInstances.Count} cadLinks={linkedCadInstances.Count} " +
                            $"grids={grids.Count} scopeBoxes={scopeBoxes.Count}");
            return result;
        }

        /// <summary>
        /// Finds the design option named <paramref name="optionName"/> whose option set is named
        /// <paramref name="setName"/>. Returns null when it does not exist.
        /// </summary>
        private static DesignOption FindDesignOption(Document doc, string setName, string optionName)
        {
            List<DesignOption> designOptions = new FilteredElementCollector(doc)
                .OfClass(typeof(DesignOption))
                .Cast<DesignOption>()
                .ToList();

            foreach (DesignOption option in designOptions)
            {
                // Element.Name on a DesignOption comes back as e.g. "LOCKED  <primary>",
                // so prefer the OPTION_NAME parameter and strip the suffix as a fallback.
                string name = GetCleanName(option, BuiltInParameter.OPTION_NAME);
                if (!string.Equals(name, optionName, StringComparison.OrdinalIgnoreCase)) continue;

                Parameter setParam = option.get_Parameter(BuiltInParameter.OPTION_SET_ID);
                if (setParam == null) continue;

                Element optionSet = doc.GetElement(setParam.AsElementId());
                if (optionSet == null) continue;

                string setNameFound = GetCleanName(optionSet, BuiltInParameter.OPTION_SET_NAME);
                if (string.Equals(setNameFound, setName, StringComparison.OrdinalIgnoreCase))
                    return option;
            }

            return null;
        }

        private static string GetCleanName(Element e, BuiltInParameter nameParam)
        {
            Parameter p = e.get_Parameter(nameParam);
            string name = p != null && p.HasValue ? p.AsString() : null;
            if (string.IsNullOrWhiteSpace(name)) name = e.Name ?? string.Empty;

            int suffix = name.IndexOf('<');
            if (suffix >= 0) name = name.Substring(0, suffix);

            return name.Trim();
        }

        /// <summary>
        /// Pins every element in the sequence that is not already pinned.
        /// Returns the ids of the elements newly pinned this run.
        /// </summary>
        private static HashSet<ElementId> PinElements(Document doc, IEnumerable<Element> elements)
        {
            HashSet<ElementId> pinned = new HashSet<ElementId>();
            List<Element> toPin = elements.Where(e => !e.Pinned).ToList();
            if (toPin.Count == 0)
            {
                Debug.WriteLine($"{LogTag} nothing to pin");
                return pinned;
            }

            using (Transaction t = new Transaction(doc, "Pin & Lock: pin elements"))
            {
                t.Start();
                foreach (Element e in toPin)
                {
                    try
                    {
                        e.Pinned = true;
                        pinned.Add(e.Id);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"{LogTag} Could not pin {e.Id} ({e.Category?.Name}): {ex.Message}");
                    }
                }
                t.Commit();
            }
            Debug.WriteLine($"{LogTag} pinned {pinned.Count} of {toPin.Count} element(s)");
            return pinned;
        }

        /// <summary>
        /// Unpins every element in the sequence that is currently pinned.
        /// Returns how many were unpinned.
        /// </summary>
        private static int UnpinElements(Document doc, IEnumerable<Element> elements)
        {
            List<Element> toUnpin = elements.Where(e => e.Pinned).ToList();
            if (toUnpin.Count == 0) return 0;

            int unpinned = 0;
            using (Transaction t = new Transaction(doc, "Pin & Lock: unpin elements"))
            {
                t.Start();
                foreach (Element e in toUnpin)
                {
                    try
                    {
                        e.Pinned = false;
                        unpinned++;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"{LogTag} Could not unpin {e.Id} ({e.Category?.Name}): {ex.Message}");
                    }
                }
                t.Commit();
            }
            Debug.WriteLine($"{LogTag} unpinned {unpinned} of {toUnpin.Count} element(s)");
            return unpinned;
        }

        /// <summary>
        /// Display group for the report: the element kinds this command handles, in a fixed order.
        /// </summary>
        private static readonly string[] ReportOrder = { "Revit Links", "CAD Links", "Grids", "Scope Boxes", "Levels" };

        private static string KindOf(Element e)
        {
            if (e is RevitLinkInstance) return "Revit Links";
            if (e is ImportInstance) return "CAD Links";
            if (e is Grid) return "Grids";
            if (e is Level) return "Levels";
            return e.Category?.Name ?? "Other";
        }

        /// <summary>
        /// One row per element kind (found, pinned now, already pinned, to / in LOCKED)
        /// in a fixed display order, followed by a bold total row.
        /// </summary>
        private static List<PinLockReportRow> BuildReportRows(
            List<Element> elements,
            HashSet<ElementId> pinnedNow,
            HashSet<ElementId> toLocked,
            HashSet<ElementId> alreadyInLocked)
        {
            Dictionary<string, List<Element>> groups = elements
                .GroupBy(KindOf)
                .ToDictionary(g => g.Key, g => g.ToList());

            IEnumerable<string> kinds = ReportOrder
                .Concat(groups.Keys.Where(k => !ReportOrder.Contains(k)).OrderBy(k => k))
                .Where(groups.ContainsKey);

            List<PinLockReportRow> rows = new List<PinLockReportRow>();
            int totalToLocked = 0;
            int totalInLocked = 0;

            foreach (string kind in kinds)
            {
                List<Element> items = groups[kind];
                int pinned = items.Count(e => pinnedNow.Contains(e.Id));
                bool allowedInOption = kind != "Levels";
                int adding = allowedInOption ? items.Count(e => toLocked.Contains(e.Id)) : 0;
                int inLocked = allowedInOption ? items.Count(e => alreadyInLocked.Contains(e.Id)) : 0;
                totalToLocked += adding;
                totalInLocked += inLocked;

                rows.Add(new PinLockReportRow
                {
                    Kind = kind,
                    Found = items.Count,
                    PinnedNow = pinned,
                    AlreadyPinned = items.Count - pinned,
                    ToLocked = allowedInOption ? adding.ToString() : "n/a",
                    InLocked = allowedInOption ? inLocked.ToString() : "n/a",
                    Elements = items
                });
            }

            rows.Add(new PinLockReportRow
            {
                Kind = "Total",
                Found = elements.Count,
                PinnedNow = pinnedNow.Count,
                AlreadyPinned = elements.Count - pinnedNow.Count,
                ToLocked = totalToLocked.ToString(),
                InLocked = totalInLocked.ToString(),
                IsTotal = true
            });

            return rows;
        }

        internal static PushButtonData GetButtonData()
        {
            string buttonInternalName = "btnLockedDesignOptions";
            string buttonTitle = "Pin & Lock";

            Common.ButtonDataClass myButtonData = new Common.ButtonDataClass(
                buttonInternalName,
                buttonTitle,
                MethodBase.GetCurrentMethod().DeclaringType?.FullName,
                Properties.Resources.LockedDesignOptions_32x32,
                Properties.Resources.LockedDesignOptions_16x16,
                Properties.Resources.LockedDesignOptions_Dark_32x32,
                Properties.Resources.LockedDesignOptions_Dark_16x16,
                $"Pins all Revit links, CAD links, grids, scope boxes and levels, then adds the links, grids and scope boxes to the {OptionSetName} > {OptionName} design option.");

            return myButtonData.Data;
        }
    }
}
