using System.Collections.Generic;
using System.Linq;

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
                int alreadyInLocked = candidates.Count(e => e.DesignOption != null && e.DesignOption.Id == lockedOption.Id);
                int inOtherOption = candidates.Count - inMainModel.Count - alreadyInLocked;

                Debug.WriteLine($"{LogTag} candidates={candidates.Count} mainModel={inMainModel.Count} " +
                                $"alreadyLocked={alreadyInLocked} otherOption={inOtherOption}");

                if (inMainModel.Count == 0)
                {
                    int levelsPinned = PinAllLevels(doc);
                    TaskDialog.Show("Locked Design Options",
                        "No Revit links, CAD links, grids or scope boxes were found in the main model.\n\n" +
                        $"Already in {OptionSetName} > {OptionName}: {alreadyInLocked}\n" +
                        $"In another design option: {inOtherOption}\n" +
                        $"Levels newly pinned: {levelsPinned}");
                    return Result.Succeeded;
                }

                // 3. Pin everything, plus all levels. Levels are pinned only: Revit does
                //    not allow levels inside a design option, so they stay out of the selection.
                List<Level> levels = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .ToList();
                Debug.WriteLine($"{LogTag} levels={levels.Count}");

                int pinnedNow = 0;
                using (Transaction t = new Transaction(doc, "Pin elements for LOCKED design option"))
                {
                    t.Start();
                    foreach (Element e in inMainModel.Concat(levels))
                    {
                        if (e.Pinned) continue;
                        try
                        {
                            e.Pinned = true;
                            pinnedNow++;
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"{LogTag} Could not pin {e.Id} ({e.Category?.Name}): {ex.Message}");
                        }
                    }
                    t.Commit();
                }
                Debug.WriteLine($"{LogTag} pinned {pinnedNow} element(s)");

                // 4. Select the elements and post Revit's "Add to Set" command, which
                //    opens the Add to Design Option Set dialog for this selection.
                uidoc.Selection.SetElementIds(inMainModel.Select(e => e.Id).ToList());

                string summary = BuildSummary(inMainModel);
                TaskDialog info = new TaskDialog("Locked Design Options")
                {
                    MainInstruction = $"{inMainModel.Count} element(s) pinned and selected, {levels.Count} level(s) pinned.",
                    MainContent =
                        summary +
                        $"Levels (pinned only, cannot join a design option): {levels.Count}\n" +
                        $"\nNewly pinned: {pinnedNow}\n\n" +
                        "When you click OK, Revit's \"Add to Design Option Set\" dialog will open. " +
                        $"Choose set \"{OptionSetName}\" and option \"{OptionName}\" to finish.\n\n" +
                        "(The Revit API cannot assign elements to a design option directly.)",
                    CommonButtons = TaskDialogCommonButtons.Ok | TaskDialogCommonButtons.Cancel,
                    DefaultButton = TaskDialogResult.Ok
                };

                if (info.Show() != TaskDialogResult.Ok)
                {
                    Debug.WriteLine($"{LogTag} user cancelled before AddToSet");
                    return Result.Succeeded;
                }

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
        /// Pins every level in the document. Returns how many were newly pinned.
        /// </summary>
        private static int PinAllLevels(Document doc)
        {
            List<Level> levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .Where(l => !l.Pinned)
                .ToList();
            if (levels.Count == 0) return 0;

            int pinned = 0;
            using (Transaction t = new Transaction(doc, "Pin levels"))
            {
                t.Start();
                foreach (Level l in levels)
                {
                    try { l.Pinned = true; pinned++; }
                    catch (Exception ex) { Debug.WriteLine($"{LogTag} Could not pin level {l.Id}: {ex.Message}"); }
                }
                t.Commit();
            }
            Debug.WriteLine($"{LogTag} pinned {pinned} level(s)");
            return pinned;
        }

        private static string BuildSummary(List<Element> elems)
        {
            int revitLinks = elems.OfType<RevitLinkInstance>().Count();
            int cadLinks = elems.OfType<ImportInstance>().Count();
            int grids = elems.OfType<Grid>().Count();
            int scopeBoxes = elems.Count - revitLinks - cadLinks - grids;

            return $"Revit links: {revitLinks}\n" +
                   $"CAD links: {cadLinks}\n" +
                   $"Grids: {grids}\n" +
                   $"Scope boxes: {scopeBoxes}\n";
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
