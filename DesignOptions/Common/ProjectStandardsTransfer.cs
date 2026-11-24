using System.Collections.Generic;
using System.Linq;
using RevitApplication = Autodesk.Revit.ApplicationServices.Application;
using RevitBinding = Autodesk.Revit.DB.Binding;

namespace DesignOptions.Common
{
    /// <summary>
    /// What happened when one standard was transferred.
    /// </summary>
    internal class TransferOutcome
    {
        public int Created;
        public int Updated;
        public int Skipped;
        public List<string> Notes { get; } = new List<string>();

        public void Note(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            // A running count re-notes with a new number: keep only the latest of such a note.
            int paren = text.IndexOf(' ');
            if (paren > 0 && int.TryParse(text.Substring(0, paren), out _))
            {
                string tail = text.Substring(paren);
                Notes.RemoveAll(n => n.EndsWith(tail, StringComparison.Ordinal));
            }
            if (!Notes.Contains(text)) Notes.Add(text);
        }
    }

    /// <summary>
    /// Copies project standards from one open document into another, Revit's
    /// "Transfer Project Standards" with the Overwrite option, as far as the API allows.
    ///
    /// Strategy per standard:
    ///  - Named element types (text, dimension, grid, level, viewport types...): types missing
    ///    from the target are copied with ElementTransformUtils.CopyElements; types that already
    ///    exist by name are overwritten by copying every writable parameter from the source type.
    ///    ElementId-valued parameters are re-pointed to the same-named element in the target,
    ///    copying it over first when it does not exist (line patterns, arrowheads, materials).
    ///  - Line styles and object styles are applied through the Category API (weights, colour,
    ///    line pattern, material) because subcategories cannot be copied as elements.
    ///  - DWG export setups, phase filters, print settings, revision settings and shared project
    ///    parameters use their dedicated APIs.
    ///
    /// The caller owns the transaction: every Run() call must happen inside an open transaction
    /// on the target document.
    /// </summary>
    internal class ProjectStandardsTransfer
    {
        private const string LogTag = "[TransferStandards]";

        private readonly Document _src;
        private readonly Document _tgt;

        /// <summary>Source element id → target element id, for elements already matched or copied this run.</summary>
        private readonly Dictionary<ElementId, ElementId> _idMap = new Dictionary<ElementId, ElementId>();

        /// <summary>Object styles whose source pattern is "none", which the API cannot reproduce.</summary>
        private int _noPatternKept;

        public ProjectStandardsTransfer(Document source, Document target)
        {
            _src = source ?? throw new ArgumentNullException(nameof(source));
            _tgt = target ?? throw new ArgumentNullException(nameof(target));
            Debug.WriteLine($"{LogTag} source=\"{_src.Title}\" target=\"{_tgt.Title}\"");
        }

        public TransferOutcome Run(ProjectStandardKind kind)
        {
            Debug.WriteLine($"{LogTag} Run {kind}");
            switch (kind)
            {
                case ProjectStandardKind.ArrowheadStyles:
                    return TransferTypes("Arrowhead Styles", doc => TypesOfFamily(doc, "Arrowhead"));
                case ProjectStandardKind.BrowserOrganization:
                    return TransferBrowserOrganization();
                case ProjectStandardKind.DimensionStyles:
                    return TransferTypes("Dimension Styles", CollectDimensionStyles, OverwriteDimensionExtras);
                case ProjectStandardKind.DwgExportSetups:
                    return TransferDwgExportSetups();
                case ProjectStandardKind.ElevationTagTypes:
                    return TransferTypes("Elevation Tag Types", doc => TypesOfFamily(doc, "Elevation Tag"));
                case ProjectStandardKind.GridTypes:
                    return TransferTypes("Grid Types", doc => TypesOfClass(doc, typeof(GridType)));
                case ProjectStandardKind.LevelTypes:
                    return TransferTypes("Level Types", doc => TypesOfClass(doc, typeof(LevelType)));
                case ProjectStandardKind.LineStyles:
                    return TransferLineStyles();
                case ProjectStandardKind.ModelTextTypes:
                    return TransferTypes("Model Text Types", doc => TypesOfClass(doc, typeof(ModelTextType)));
                case ProjectStandardKind.ObjectStyles:
                    return TransferObjectStyles();
                case ProjectStandardKind.PhaseSettings:
                    return TransferPhaseSettings();
                case ProjectStandardKind.PrintSettings:
                    return TransferPrintSettings();
                case ProjectStandardKind.ProjectParameters:
                    return TransferProjectParameters();
                case ProjectStandardKind.RevisionSettings:
                    return TransferRevisionSettings();
                case ProjectStandardKind.SpotDimensionSettings:
                    return TransferTypes("Spot Dimension Settings", CollectSpotDimensionStyles, OverwriteDimensionExtras);
                case ProjectStandardKind.TextTypes:
                    return TransferTypes("Text Types", doc => TypesOfClass(doc, typeof(TextNoteType)));
                case ProjectStandardKind.ViewReferenceTypes:
                    return TransferTypes("View Reference Types", doc => TypesOfCategoryOrFamily(doc, BuiltInCategory.OST_ReferenceViewer, "View Reference"));
                case ProjectStandardKind.ViewportTypes:
                    return TransferTypes("Viewport Types", doc => TypesOfCategoryOrFamily(doc, BuiltInCategory.OST_Viewports, "Viewport"));
                case ProjectStandardKind.LineWeights:
                default:
                    throw new NotSupportedException($"{kind} cannot be transferred through the Revit API.");
            }
        }

        // ------------------------------------------------------------------
        // Collectors
        // ------------------------------------------------------------------

        private static List<ElementType> TypesOfClass(Document doc, Type cls)
        {
            return new FilteredElementCollector(doc).OfClass(cls).Cast<ElementType>().ToList();
        }

        private static List<ElementType> TypesOfFamily(Document doc, string familyName)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(ElementType))
                .Cast<ElementType>()
                .Where(t => string.Equals(t.FamilyName, familyName, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        private static List<ElementType> TypesOfCategory(Document doc, BuiltInCategory bic)
        {
            return new FilteredElementCollector(doc)
                .OfCategory(bic)
                .WhereElementIsElementType()
                .Cast<ElementType>()
                .ToList();
        }

        /// <summary>
        /// Types whose category is <paramref name="bic"/> or whose family name is
        /// <paramref name="familyName"/>: viewport and view reference types report
        /// their category inconsistently, so either match counts.
        /// </summary>
        private static List<ElementType> TypesOfCategoryOrFamily(Document doc, BuiltInCategory bic, string familyName)
        {
            Dictionary<ElementId, ElementType> found = new Dictionary<ElementId, ElementType>();
            try { foreach (ElementType t in TypesOfCategory(doc, bic)) found[t.Id] = t; }
            catch (Exception ex) { Debug.WriteLine($"{LogTag} TypesOfCategory {bic}: {ex.Message}"); }
            foreach (ElementType t in TypesOfFamily(doc, familyName)) found[t.Id] = t;
            return found.Values.ToList();
        }

        private static bool IsSpotStyle(DimensionType d)
        {
            return d.StyleType == DimensionStyleType.SpotElevation
                || d.StyleType == DimensionStyleType.SpotCoordinate
                || d.StyleType == DimensionStyleType.SpotSlope;
        }

        private static List<ElementType> CollectDimensionStyles(Document doc)
        {
            return new FilteredElementCollector(doc).OfClass(typeof(DimensionType)).Cast<DimensionType>()
                .Where(d => !IsSpotStyle(d)).Cast<ElementType>().ToList();
        }

        private static List<ElementType> CollectSpotDimensionStyles(Document doc)
        {
            return new FilteredElementCollector(doc).OfClass(typeof(DimensionType)).Cast<DimensionType>()
                .Where(IsSpotStyle).Cast<ElementType>().ToList();
        }

        // ------------------------------------------------------------------
        // Generic named-type transfer
        // ------------------------------------------------------------------

        private static string KeyOf(ElementType t)
        {
            return (t.FamilyName ?? string.Empty) + "|" + SafeName(t);
        }

        private static string SafeName(Element e)
        {
            try { return e.Name ?? string.Empty; }
            catch { return string.Empty; }
        }

        private static Dictionary<string, T> ByKey<T>(IEnumerable<T> items, Func<T, string> key) where T : Element
        {
            Dictionary<string, T> map = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
            foreach (T item in items)
            {
                string k = key(item);
                if (!map.ContainsKey(k)) map[k] = item;
            }
            return map;
        }

        /// <summary>
        /// Copies the types that are missing from the target and overwrites (by parameter copy)
        /// the ones that already exist with the same family and type name.
        /// </summary>
        private TransferOutcome TransferTypes(string label, Func<Document, List<ElementType>> collect, Action<ElementType, ElementType> extraOverwrite = null)
        {
            TransferOutcome outcome = new TransferOutcome();
            List<ElementType> srcTypes = collect(_src);
            Dictionary<string, ElementType> tgtByKey = ByKey(collect(_tgt), KeyOf);

            List<ElementType> toCopy = new List<ElementType>();
            List<KeyValuePair<ElementType, ElementType>> toUpdate = new List<KeyValuePair<ElementType, ElementType>>();
            foreach (ElementType s in srcTypes)
            {
                if (tgtByKey.TryGetValue(KeyOf(s), out ElementType t)) toUpdate.Add(new KeyValuePair<ElementType, ElementType>(s, t));
                else toCopy.Add(s);
            }
            Debug.WriteLine($"{LogTag} {label}: source={srcTypes.Count} new={toCopy.Count} existing={toUpdate.Count}");

            if (toCopy.Count > 0)
            {
                CopyElementsResilient(toCopy.Select(e => e.Id).ToList(), outcome);
                Dictionary<string, ElementType> after = ByKey(collect(_tgt), KeyOf);
                foreach (ElementType s in toCopy)
                {
                    if (after.TryGetValue(KeyOf(s), out ElementType created))
                    {
                        outcome.Created++;
                        _idMap[s.Id] = created.Id;
                    }
                    else
                    {
                        outcome.Skipped++;
                        outcome.Note($"\"{SafeName(s)}\" could not be copied.");
                    }
                }
            }

            foreach (KeyValuePair<ElementType, ElementType> pair in toUpdate)
            {
                try
                {
                    _idMap[pair.Key.Id] = pair.Value.Id;
                    CopyParameters(pair.Key, pair.Value, outcome);
                    extraOverwrite?.Invoke(pair.Key, pair.Value);
                    outcome.Updated++;
                }
                catch (Exception ex)
                {
                    outcome.Skipped++;
                    outcome.Note($"Could not update \"{SafeName(pair.Key)}\": {ex.Message}");
                    Debug.WriteLine($"{LogTag} {label}: update of {pair.Key.Id} failed: {ex}");
                }
            }

            return outcome;
        }

        /// <summary>Dimension type settings that are not exposed as parameters.</summary>
        private static void OverwriteDimensionExtras(ElementType source, ElementType target)
        {
            DimensionType s = source as DimensionType;
            DimensionType t = target as DimensionType;
            if (s == null || t == null) return;

            // Revit's own system styles ("Linear Dimension Style" etc., named after their family)
            // keep the target project's unit format in the built-in transfer, so do the same.
            if (string.Equals(SafeName(s), s.FamilyName, StringComparison.OrdinalIgnoreCase)) return;

            Try(() => t.SetUnitsFormatOptions(s.GetUnitsFormatOptions()), "units format");
            Try(() => t.AlternateUnits = s.AlternateUnits, "alternate units");
            Try(() => t.SetAlternateUnitsFormatOptions(s.GetAlternateUnitsFormatOptions()), "alternate units format");
            Try(() => t.AlternateUnitsPrefix = s.AlternateUnitsPrefix, "alternate units prefix");
            Try(() => t.AlternateUnitsSuffix = s.AlternateUnitsSuffix, "alternate units suffix");
            Try(() => t.Prefix = s.Prefix, "prefix");
            Try(() => t.Suffix = s.Suffix, "suffix");
            Try(() => { if (s.CanHaveEqualityFormula() && t.CanHaveEqualityFormula()) t.SetEqualityFormula(s.GetEqualityFormula()); }, "equality formula");
            Try(() => { if (s.CanHaveOrdinateDimensionSetting() && t.CanHaveOrdinateDimensionSetting()) t.SetOrdinateDimensionSetting(s.GetOrdinateDimensionSetting()); }, "ordinate setting");
        }

        // ------------------------------------------------------------------
        // Parameter copy (overwrite existing element from source element)
        // ------------------------------------------------------------------

        private static readonly HashSet<BuiltInParameter> SkipParameters = new HashSet<BuiltInParameter>
        {
            BuiltInParameter.ALL_MODEL_TYPE_NAME,
            BuiltInParameter.SYMBOL_NAME_PARAM,
            BuiltInParameter.ALL_MODEL_FAMILY_NAME,
            BuiltInParameter.SYMBOL_FAMILY_NAME_PARAM,
            BuiltInParameter.ELEM_FAMILY_PARAM,
            BuiltInParameter.ELEM_TYPE_PARAM,
            BuiltInParameter.ELEM_FAMILY_AND_TYPE_PARAM,
            BuiltInParameter.ELEM_CATEGORY_PARAM,
            BuiltInParameter.ELEM_CATEGORY_PARAM_MT,
            BuiltInParameter.ALL_MODEL_IMAGE,
            BuiltInParameter.ALL_MODEL_TYPE_IMAGE,
            BuiltInParameter.DESIGN_OPTION_ID,
            BuiltInParameter.EDITED_BY,
            BuiltInParameter.ID_PARAM,
            BuiltInParameter.ELEM_PARTITION_PARAM
        };

        /// <summary>
        /// Writes every writable parameter value of <paramref name="source"/> onto
        /// <paramref name="target"/>. ElementId values are mapped to the target document.
        /// </summary>
        private void CopyParameters(Element source, Element target, TransferOutcome outcome)
        {
            foreach (Parameter sp in source.Parameters)
            {
                if (sp.IsReadOnly || !sp.HasValue) continue;
                InternalDefinition def = sp.Definition as InternalDefinition;
                if (def == null) continue;
                if (def.BuiltInParameter != BuiltInParameter.INVALID && SkipParameters.Contains(def.BuiltInParameter)) continue;

                Parameter tp = null;
                try
                {
                    if (def.BuiltInParameter != BuiltInParameter.INVALID) tp = target.get_Parameter(def.BuiltInParameter);
                    else if (sp.IsShared) tp = target.get_Parameter(sp.GUID);
                    else tp = target.LookupParameter(def.Name);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"{LogTag} lookup of parameter \"{def.Name}\" failed: {ex.Message}");
                }
                if (tp == null || tp.IsReadOnly || tp.StorageType != sp.StorageType) continue;

                try
                {
                    switch (sp.StorageType)
                    {
                        case StorageType.String:
                            {
                                string v = sp.AsString();
                                if (tp.AsString() != v) tp.Set(v ?? string.Empty);
                                break;
                            }
                        case StorageType.Integer:
                            if (tp.AsInteger() != sp.AsInteger()) tp.Set(sp.AsInteger());
                            break;
                        case StorageType.Double:
                            if (Math.Abs(tp.AsDouble() - sp.AsDouble()) > 1e-9) tp.Set(sp.AsDouble());
                            break;
                        case StorageType.ElementId:
                            {
                                ElementId mapped = MapId(sp.AsElementId(), outcome);
                                if (mapped != null && tp.AsElementId() != mapped) tp.Set(mapped);
                                break;
                            }
                    }
                }
                catch (Exception ex)
                {
                    // One stubborn parameter must not stop the rest of the type from updating.
                    Debug.WriteLine($"{LogTag} set of \"{def.Name}\" on {target.Id} failed: {ex.Message}");
                }
            }
        }

        // ------------------------------------------------------------------
        // ElementId mapping between the documents
        // ------------------------------------------------------------------

        private static long IdValue(ElementId id)
        {
#if REVIT2024 || REVIT2025 || REVIT2026
            return id.Value;
#else
            return id.IntegerValue;
#endif
        }

        /// <summary>
        /// Finds (or copies over) the target-document counterpart of a source element id.
        /// Returns null when there is none; InvalidElementId maps to itself.
        /// </summary>
        private ElementId MapId(ElementId sourceId, TransferOutcome outcome)
        {
            if (sourceId == null || sourceId == ElementId.InvalidElementId) return ElementId.InvalidElementId;
            // Negative ids are built-ins (categories, the Solid line pattern...) shared by every document.
            if (IdValue(sourceId) < 0) return sourceId;
            if (_idMap.TryGetValue(sourceId, out ElementId known)) return known;

            Element se = _src.GetElement(sourceId);
            if (se == null) return null;

            Element te = FindCounterpart(se);
            if (te == null && IsCopyableDependency(se))
            {
                try
                {
                    CopyElements(new List<ElementId> { sourceId });
                    te = FindCounterpart(se);
                    Debug.WriteLine($"{LogTag} copied dependency {se.GetType().Name} \"{SafeName(se)}\" -> {(te == null ? "not found" : te.Id.ToString())}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"{LogTag} could not copy dependency {se.GetType().Name} \"{SafeName(se)}\": {ex.Message}");
                    outcome?.Note($"\"{SafeName(se)}\" ({se.Category?.Name ?? se.GetType().Name}) could not be copied; references to it were left unchanged.");
                }
            }

            if (te == null) return null;
            _idMap[sourceId] = te.Id;
            return te.Id;
        }

        private static bool IsCopyableDependency(Element e)
        {
            return e is ElementType || e is LinePatternElement || e is FillPatternElement || e is Material;
        }

        /// <summary>Same class and name (and family name for types) in the target document.</summary>
        private Element FindCounterpart(Element se)
        {
            string name = SafeName(se);
            if (string.IsNullOrEmpty(name)) return null;
            string family = (se as ElementType)?.FamilyName;

            IEnumerable<Element> candidates;
            try
            {
                candidates = new FilteredElementCollector(_tgt).OfClass(se.GetType()).ToElements();
            }
            catch
            {
                if (se.Category == null) return null;
                FilteredElementCollector c = new FilteredElementCollector(_tgt).OfCategoryId(se.Category.Id);
                candidates = (se is ElementType ? c.WhereElementIsElementType() : c.WhereElementIsNotElementType()).ToElements();
            }

            foreach (Element te in candidates)
            {
                if (!string.Equals(SafeName(te), name, StringComparison.OrdinalIgnoreCase)) continue;
                if (family != null && !string.Equals((te as ElementType)?.FamilyName, family, StringComparison.OrdinalIgnoreCase)) continue;
                return te;
            }
            return null;
        }

        // ------------------------------------------------------------------
        // CopyElements helpers
        // ------------------------------------------------------------------

        private class UseDestinationTypesHandler : IDuplicateTypeNamesHandler
        {
            public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args)
            {
                return DuplicateTypeAction.UseDestinationTypes;
            }
        }

        private ICollection<ElementId> CopyElements(ICollection<ElementId> ids)
        {
            using (CopyPasteOptions opts = new CopyPasteOptions())
            {
                opts.SetDuplicateTypeNamesHandler(new UseDestinationTypesHandler());
                return ElementTransformUtils.CopyElements(_src, ids, _tgt, Transform.Identity, opts);
            }
        }

        /// <summary>Copies as a batch; if that fails, one at a time so a single bad element does not block the rest.</summary>
        private void CopyElementsResilient(ICollection<ElementId> ids, TransferOutcome outcome)
        {
            if (ids.Count == 0) return;
            try
            {
                CopyElements(ids);
                return;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"{LogTag} batch copy of {ids.Count} failed, retrying one by one: {ex.Message}");
            }

            foreach (ElementId id in ids)
            {
                try
                {
                    CopyElements(new List<ElementId> { id });
                }
                catch (Exception ex)
                {
                    Element e = _src.GetElement(id);
                    outcome.Note($"\"{SafeName(e)}\" could not be copied: {ex.Message}");
                    Debug.WriteLine($"{LogTag} copy of {id} failed: {ex.Message}");
                }
            }
        }

        private static void Try(Action action, string what)
        {
            try { action(); }
            catch (Exception ex) { Debug.WriteLine($"{LogTag} {what}: {ex.Message}"); }
        }

        // ------------------------------------------------------------------
        // Browser organization
        // ------------------------------------------------------------------

        /// <summary>"all" exists once per browser type (Views, Sheets, Schedules), so the type is part of the key.</summary>
        private static string BrowserKey(BrowserOrganization o)
        {
            return o.Type + "|" + SafeName(o);
        }

        private static List<BrowserOrganization> BrowserOrgs(Document doc)
        {
            return new FilteredElementCollector(doc).OfClass(typeof(BrowserOrganization)).Cast<BrowserOrganization>().ToList();
        }

        /// <summary>
        /// Missing organizations are copied. Existing ones cannot be edited through the API, so
        /// they are replaced: rename the old one, copy the source, delete the old. When Revit
        /// refuses the delete (typically the organization in use), the copy is removed again and
        /// the old one keeps its name.
        /// </summary>
        private TransferOutcome TransferBrowserOrganization()
        {
            TransferOutcome outcome = new TransferOutcome();
            List<BrowserOrganization> srcOrgs = BrowserOrgs(_src);
            Dictionary<string, BrowserOrganization> tgtByKey = ByKey(BrowserOrgs(_tgt), BrowserKey);

            List<BrowserOrganization> toCopy = srcOrgs.Where(o => !tgtByKey.ContainsKey(BrowserKey(o))).ToList();
            List<BrowserOrganization> toReplace = srcOrgs.Where(o => tgtByKey.ContainsKey(BrowserKey(o))).ToList();
            Debug.WriteLine($"{LogTag} Browser organization: source={srcOrgs.Count} new={toCopy.Count} existing={toReplace.Count}");

            if (toCopy.Count > 0)
            {
                CopyElementsResilient(toCopy.Select(o => o.Id).ToList(), outcome);
                Dictionary<string, BrowserOrganization> after = ByKey(BrowserOrgs(_tgt), BrowserKey);
                foreach (BrowserOrganization o in toCopy)
                {
                    if (after.ContainsKey(BrowserKey(o))) outcome.Created++;
                    else { outcome.Skipped++; outcome.Note($"\"{SafeName(o)}\" could not be copied."); }
                }
            }

            foreach (BrowserOrganization s in toReplace)
            {
                BrowserOrganization old = tgtByKey[BrowserKey(s)];
                string name = SafeName(s);
                string label = $"{name} ({s.Type})";
                string tempName = name + " (old)";
                bool renamed = false;
                try
                {
                    old.Name = tempName;
                    renamed = true;
                    CopyElements(new List<ElementId> { s.Id });

                    BrowserOrganization copy = BrowserOrgs(_tgt).FirstOrDefault(o => o.Id != old.Id && BrowserKey(o) == BrowserKey(s));
                    if (copy == null)
                    {
                        old.Name = name;
                        outcome.Skipped++;
                        outcome.Note($"\"{label}\" could not be copied; the existing one was kept.");
                        continue;
                    }

                    try
                    {
                        _tgt.Delete(old.Id);
                        outcome.Updated++;
                    }
                    catch (Exception delEx)
                    {
                        Debug.WriteLine($"{LogTag} delete of browser organization \"{label}\" refused: {delEx.Message}");
                        _tgt.Delete(copy.Id);
                        old.Name = name;
                        outcome.Skipped++;
                        outcome.Note($"\"{label}\" is in use and could not be replaced; set another organization current and run again.");
                    }
                }
                catch (Exception ex)
                {
                    if (renamed) Try(() => old.Name = name, $"restore name of {label}");
                    outcome.Skipped++;
                    outcome.Note($"\"{label}\": {ex.Message}");
                    Debug.WriteLine($"{LogTag} replace of browser organization \"{label}\" failed: {ex}");
                }
            }
            return outcome;
        }

        // ------------------------------------------------------------------
        // DWG / DXF export setups
        // ------------------------------------------------------------------

        private TransferOutcome TransferDwgExportSetups()
        {
            TransferOutcome outcome = new TransferOutcome();
            List<ExportDWGSettings> srcSetups = new FilteredElementCollector(_src).OfClass(typeof(ExportDWGSettings)).Cast<ExportDWGSettings>().ToList();
            Debug.WriteLine($"{LogTag} DWG export setups: source={srcSetups.Count}");

            foreach (ExportDWGSettings s in srcSetups)
            {
                string name = SafeName(s);
                try
                {
                    DWGExportOptions options = s.GetDWGExportOptions();
                    ExportDWGSettings existing = ExportDWGSettings.FindByName(_tgt, name);
                    if (existing == null)
                    {
                        ExportDWGSettings.Create(_tgt, name, options);
                        outcome.Created++;
                    }
                    else
                    {
                        existing.SetDWGExportOptions(options);
                        outcome.Updated++;
                    }
                }
                catch (Exception ex)
                {
                    outcome.Skipped++;
                    outcome.Note($"Export setup \"{name}\": {ex.Message}");
                    Debug.WriteLine($"{LogTag} export setup \"{name}\" failed: {ex}");
                }
            }
            return outcome;
        }

        // ------------------------------------------------------------------
        // Line styles and object styles (Category API)
        // ------------------------------------------------------------------

        private TransferOutcome TransferLineStyles()
        {
            TransferOutcome outcome = new TransferOutcome();
            Category srcLines = _src.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
            Category tgtLines = _tgt.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
            TransferSubcategories(srcLines, tgtLines, false, outcome);
            return outcome;
        }

        private TransferOutcome TransferObjectStyles()
        {
            TransferOutcome outcome = new TransferOutcome();
            Dictionary<long, Category> tgtById = new Dictionary<long, Category>();
            foreach (Category c in _tgt.Settings.Categories) tgtById[IdValue(c.Id)] = c;

            foreach (Category sc in _src.Settings.Categories)
            {
                if (sc.CategoryType != CategoryType.Model && sc.CategoryType != CategoryType.Annotation && sc.CategoryType != CategoryType.AnalyticalModel) continue;
                if (IdValue(sc.Id) == (long)BuiltInCategory.OST_ImportObjectStyles) continue; // DWG layer styles belong to specific imports

                if (!tgtById.TryGetValue(IdValue(sc.Id), out Category tc)) continue;

                ApplyCategoryGraphics(sc, tc, true, outcome);
                outcome.Updated++;
                TransferSubcategories(sc, tc, true, outcome);
            }
            return outcome;
        }

        private void TransferSubcategories(Category srcParent, Category tgtParent, bool includeMaterial, TransferOutcome outcome)
        {
            Dictionary<string, Category> tgtSubs = new Dictionary<string, Category>(StringComparer.OrdinalIgnoreCase);
            foreach (Category c in tgtParent.SubCategories) if (!tgtSubs.ContainsKey(c.Name)) tgtSubs[c.Name] = c;

            foreach (Category sc in srcParent.SubCategories)
            {
                bool created = false;
                if (!tgtSubs.TryGetValue(sc.Name, out Category tc))
                {
                    if (!tgtParent.CanAddSubcategory)
                    {
                        outcome.Skipped++;
                        outcome.Note($"\"{tgtParent.Name}\" does not accept new subcategories; \"{sc.Name}\" was skipped.");
                        continue;
                    }
                    try
                    {
                        tc = _tgt.Settings.Categories.NewSubcategory(tgtParent, sc.Name);
                        tgtSubs[sc.Name] = tc;
                        created = true;
                    }
                    catch (Exception ex)
                    {
                        outcome.Skipped++;
                        outcome.Note($"Could not create \"{tgtParent.Name} > {sc.Name}\": {ex.Message}");
                        Debug.WriteLine($"{LogTag} NewSubcategory {tgtParent.Name}>{sc.Name} failed: {ex.Message}");
                        continue;
                    }
                }

                ApplyCategoryGraphics(sc, tc, includeMaterial, outcome);
                if (created) outcome.Created++; else outcome.Updated++;
            }
        }

        private void ApplyCategoryGraphics(Category sc, Category tc, bool includeMaterial, TransferOutcome outcome)
        {
            foreach (GraphicsStyleType gst in new[] { GraphicsStyleType.Projection, GraphicsStyleType.Cut })
            {
                try
                {
                    int? weight = sc.GetLineWeight(gst);
                    if (weight.HasValue && tc.GetLineWeight(gst) != weight) tc.SetLineWeight(weight.Value, gst);
                }
                catch (Exception ex) { Debug.WriteLine($"{LogTag} {tc.Name} {gst} weight: {ex.Message}"); }

                try
                {
                    ElementId pattern = sc.GetLinePatternId(gst);
                    if (pattern == null || pattern == ElementId.InvalidElementId)
                    {
                        // Source has no line pattern. Revit's own transfer reproduces that, but
                        // SetLinePatternId rejects InvalidElementId (verified on 2025), so the
                        // target keeps its pattern and the row reports how many were affected.
                        ElementId current = tc.GetLinePatternId(gst);
                        if (current != null && current != ElementId.InvalidElementId)
                        {
                            _noPatternKept++;
                            outcome.Note($"{_noPatternKept} object style(s) have no line pattern in the source; the API cannot clear a pattern, so the target's was kept (lines still draw solid).");
                        }
                    }
                    else
                    {
                        ElementId mapped = MapId(pattern, outcome);
                        if (mapped != null && mapped != ElementId.InvalidElementId && tc.GetLinePatternId(gst) != mapped)
                            tc.SetLinePatternId(mapped, gst);
                    }
                }
                catch (Exception ex) { Debug.WriteLine($"{LogTag} {tc.Name} {gst} pattern: {ex.Message}"); }
            }

            try
            {
                Color c = sc.LineColor;
                if (c != null && c.IsValid) tc.LineColor = new Color(c.Red, c.Green, c.Blue);
            }
            catch (Exception ex) { Debug.WriteLine($"{LogTag} {tc.Name} colour: {ex.Message}"); }

            if (!includeMaterial) return;
            try
            {
                Material m = sc.Material;
                if (m == null)
                {
                    // Overwrite means the target ends up like the source: no material.
                    if (tc.Material != null) tc.Material = null;
                    return;
                }
                ElementId mapped = MapId(m.Id, outcome);
                if (mapped == null || mapped == ElementId.InvalidElementId) return;
                if (tc.Material == null || tc.Material.Id != mapped) tc.Material = _tgt.GetElement(mapped) as Material;
            }
            catch (Exception ex) { Debug.WriteLine($"{LogTag} {tc.Name} material: {ex.Message}"); }
        }

        // ------------------------------------------------------------------
        // Phase settings
        // ------------------------------------------------------------------

        private TransferOutcome TransferPhaseSettings()
        {
            TransferOutcome outcome = new TransferOutcome();

            // Phases: the API has no Phase.Create, so try CopyElements and report the rest.
            HashSet<string> tgtPhaseNames = new HashSet<string>(_tgt.Phases.Cast<Phase>().Select(SafeName), StringComparer.OrdinalIgnoreCase);
            foreach (Phase p in _src.Phases.Cast<Phase>().Where(p => !tgtPhaseNames.Contains(SafeName(p))).ToList())
            {
                string name = SafeName(p);
                try
                {
                    CopyElements(new List<ElementId> { p.Id });
                    bool exists = _tgt.Phases.Cast<Phase>().Any(x => string.Equals(SafeName(x), name, StringComparison.OrdinalIgnoreCase));
                    if (exists) outcome.Created++;
                    else { outcome.Skipped++; outcome.Note($"Phase \"{name}\" could not be created; add it under Manage > Phases."); }
                }
                catch (Exception ex)
                {
                    outcome.Skipped++;
                    outcome.Note($"Phase \"{name}\" could not be created ({ex.Message}); add it under Manage > Phases.");
                }
            }

            // Phase filters: create missing, overwrite presentations on existing.
            Dictionary<string, PhaseFilter> tgtFilters = ByKey(new FilteredElementCollector(_tgt).OfClass(typeof(PhaseFilter)).Cast<PhaseFilter>(), SafeName);
            foreach (PhaseFilter sf in new FilteredElementCollector(_src).OfClass(typeof(PhaseFilter)).Cast<PhaseFilter>())
            {
                string name = SafeName(sf);
                try
                {
                    bool created = false;
                    if (!tgtFilters.TryGetValue(name, out PhaseFilter tf))
                    {
                        tf = PhaseFilter.Create(_tgt, name);
                        tgtFilters[name] = tf;
                        created = true;
                    }
                    foreach (ElementOnPhaseStatus status in Enum.GetValues(typeof(ElementOnPhaseStatus)))
                    {
                        try { tf.SetPhaseStatusPresentation(status, sf.GetPhaseStatusPresentation(status)); }
                        catch { /* statuses without a presentation (None, Past, Future) throw */ }
                    }
                    if (created) outcome.Created++; else outcome.Updated++;
                }
                catch (Exception ex)
                {
                    outcome.Skipped++;
                    outcome.Note($"Phase filter \"{name}\": {ex.Message}");
                }
            }

            outcome.Note("Phase graphic overrides (Manage > Phases > Graphic Overrides) are not reachable through the API and must be set by hand.");
            return outcome;
        }

        // ------------------------------------------------------------------
        // Print settings
        // ------------------------------------------------------------------

        private TransferOutcome TransferPrintSettings()
        {
            TransferOutcome outcome = new TransferOutcome();
            List<PrintSetting> srcSettings = new FilteredElementCollector(_src).OfClass(typeof(PrintSetting)).Cast<PrintSetting>().ToList();
            Dictionary<string, PrintSetting> tgtByName = ByKey(new FilteredElementCollector(_tgt).OfClass(typeof(PrintSetting)).Cast<PrintSetting>(), SafeName);
            Debug.WriteLine($"{LogTag} Print settings: source={srcSettings.Count} target={tgtByName.Count}");
            if (srcSettings.Count == 0) return outcome;

            PrintManager pm = _tgt.PrintManager;
            foreach (PrintSetting sps in srcSettings)
            {
                string name = SafeName(sps);
                try
                {
                    if (tgtByName.TryGetValue(name, out PrintSetting tps))
                    {
                        pm.PrintSetup.CurrentPrintSetting = tps;
                        CopyPrintParameters(sps.PrintParameters, pm.PrintSetup.CurrentPrintSetting.PrintParameters, pm);
                        pm.PrintSetup.Save();
                        outcome.Updated++;
                        continue;
                    }

                    // New: a straight element copy keeps every field; fall back to the PrintManager.
                    bool done = false;
                    try
                    {
                        CopyElements(new List<ElementId> { sps.Id });
                        done = new FilteredElementCollector(_tgt).OfClass(typeof(PrintSetting)).Any(e => string.Equals(SafeName(e), name, StringComparison.OrdinalIgnoreCase));
                    }
                    catch (Exception ex) { Debug.WriteLine($"{LogTag} print setting copy \"{name}\" failed, using PrintManager: {ex.Message}"); }

                    if (!done)
                    {
                        pm.PrintSetup.CurrentPrintSetting = pm.PrintSetup.InSession;
                        CopyPrintParameters(sps.PrintParameters, pm.PrintSetup.CurrentPrintSetting.PrintParameters, pm);
                        done = pm.PrintSetup.SaveAs(name);
                    }
                    if (done) outcome.Created++;
                    else { outcome.Skipped++; outcome.Note($"Print setting \"{name}\" could not be saved."); }
                }
                catch (Exception ex)
                {
                    outcome.Skipped++;
                    outcome.Note($"Print setting \"{name}\": {ex.Message}");
                    Debug.WriteLine($"{LogTag} print setting \"{name}\" failed: {ex}");
                }
            }
            return outcome;
        }

        private static void CopyPrintParameters(PrintParameters s, PrintParameters d, PrintManager pm)
        {
            // Paper size and source belong to the target's printer: match by name.
            Try(() =>
            {
                string paper = s.PaperSize?.Name;
                if (paper == null) return;
                foreach (PaperSize ps in pm.PaperSizes) if (ps.Name == paper) { d.PaperSize = ps; break; }
            }, "paper size");
            Try(() =>
            {
                string source = s.PaperSource?.Name;
                if (source == null) return;
                foreach (PaperSource ps in pm.PaperSources) if (ps.Name == source) { d.PaperSource = ps; break; }
            }, "paper source");

            Try(() => d.PageOrientation = s.PageOrientation, "orientation");
            Try(() => d.PaperPlacement = s.PaperPlacement, "paper placement");
            Try(() => d.MarginType = s.MarginType, "margin type");
            Try(() => d.OriginOffsetX = s.OriginOffsetX, "offset x");
            Try(() => d.OriginOffsetY = s.OriginOffsetY, "offset y");
            Try(() => d.ZoomType = s.ZoomType, "zoom type");
            Try(() => d.Zoom = s.Zoom, "zoom");
            Try(() => d.HiddenLineViews = s.HiddenLineViews, "hidden line views");
            Try(() => d.ColorDepth = s.ColorDepth, "colour depth");
            Try(() => d.RasterQuality = s.RasterQuality, "raster quality");
            Try(() => d.ViewLinksinBlue = s.ViewLinksinBlue, "view links in blue");
            Try(() => d.HideReforWorkPlanes = s.HideReforWorkPlanes, "hide ref planes");
            Try(() => d.HideUnreferencedViewTags = s.HideUnreferencedViewTags, "hide unreferenced tags");
            Try(() => d.HideScopeBoxes = s.HideScopeBoxes, "hide scope boxes");
            Try(() => d.HideCropBoundaries = s.HideCropBoundaries, "hide crop boundaries");
            Try(() => d.ReplaceHalftoneWithThinLines = s.ReplaceHalftoneWithThinLines, "halftone");
            Try(() => d.MaskCoincidentLines = s.MaskCoincidentLines, "mask coincident lines");
        }

        // ------------------------------------------------------------------
        // Project parameters (shared only)
        // ------------------------------------------------------------------

        private static HashSet<string> BoundParameterNames(Document doc)
        {
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            DefinitionBindingMapIterator it = doc.ParameterBindings.ForwardIterator();
            it.Reset();
            while (it.MoveNext())
            {
                if (it.Key != null) names.Add(it.Key.Name);
            }
            return names;
        }

        private class SharedBinding
        {
            public InternalDefinition Definition;
            public ElementBinding Binding;
            public SharedParameterElement Shared;
        }

        private TransferOutcome TransferProjectParameters()
        {
            TransferOutcome outcome = new TransferOutcome();
            List<SharedBinding> shared = new List<SharedBinding>();
            Dictionary<string, ElementId> nonShared = new Dictionary<string, ElementId>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> alreadyBound = BoundParameterNames(_tgt);

            DefinitionBindingMapIterator it = _src.ParameterBindings.ForwardIterator();
            it.Reset();
            while (it.MoveNext())
            {
                InternalDefinition def = it.Key as InternalDefinition;
                ElementBinding binding = it.Current as ElementBinding;
                if (def == null || binding == null) continue;

                SharedParameterElement spe = _src.GetElement(def.Id) as SharedParameterElement;
                if (spe == null) { nonShared[def.Name] = def.Id; continue; }
                shared.Add(new SharedBinding { Definition = def, Binding = binding, Shared = spe });
            }
            Debug.WriteLine($"{LogTag} Project parameters: shared={shared.Count} nonShared={nonShared.Count}");

            // Non-shared project parameters have no creation API. The only route is copying the
            // ParameterElement itself across documents; try it and report whatever Revit refuses.
            List<string> missingNonShared = nonShared.Keys.Where(n => !alreadyBound.Contains(n)).OrderBy(n => n).ToList();
            if (missingNonShared.Count > 0)
            {
                List<ElementId> ids = missingNonShared.Select(n => nonShared[n]).ToList();
                try { CopyElementsResilient(ids, outcome); }
                catch (Exception ex) { Debug.WriteLine($"{LogTag} non-shared parameter copy failed: {ex.Message}"); }

                HashSet<string> nowBound = BoundParameterNames(_tgt);
                List<string> still = new List<string>();
                foreach (string n in missingNonShared)
                {
                    if (nowBound.Contains(n)) outcome.Created++;
                    else still.Add(n);
                }
                Debug.WriteLine($"{LogTag} non-shared parameters: copied={missingNonShared.Count - still.Count} refused={still.Count}");
                if (still.Count > 0)
                {
                    outcome.Skipped += still.Count;
                    outcome.Note($"{still.Count} non-shared project parameter(s) could not be created (the API has no way to add them): {string.Join(", ", still)}. Add them under Manage > Project Parameters.");
                }
            }
            if (shared.Count == 0) return outcome;

            Dictionary<long, Category> tgtCategories = new Dictionary<long, Category>();
            foreach (Category c in _tgt.Settings.Categories) tgtCategories[IdValue(c.Id)] = c;

            RevitApplication app = _tgt.Application;
            string originalFile = app.SharedParametersFilename;
            string tempFile = Path.Combine(Path.GetTempPath(), $"TransferProjectStandards_{Guid.NewGuid():N}.txt");
            try
            {
                File.WriteAllText(tempFile, string.Empty);
                app.SharedParametersFilename = tempFile;
                DefinitionFile defFile = app.OpenSharedParameterFile();
                if (defFile == null) throw new InvalidOperationException("Could not open a temporary shared parameter file.");
                DefinitionGroup group = defFile.Groups.Create("TransferProjectStandards");

                foreach (SharedBinding sb in shared)
                {
                    string name = sb.Definition.Name;
                    try
                    {
                        ExternalDefinitionCreationOptions options = new ExternalDefinitionCreationOptions(name, sb.Definition.GetDataType())
                        {
                            GUID = sb.Shared.GuidValue,
                            Visible = sb.Definition.Visible,
                            UserModifiable = true,
                            HideWhenNoValue = sb.Shared.ShouldHideWhenNoValue()
                        };
                        ExternalDefinition ext = group.Definitions.Create(options) as ExternalDefinition;
                        if (ext == null) throw new InvalidOperationException("definition could not be created in the temporary shared parameter file");

                        CategorySet cats = app.Create.NewCategorySet();
                        foreach (Category c in sb.Binding.Categories)
                        {
                            if (tgtCategories.TryGetValue(IdValue(c.Id), out Category tc) && tc.AllowsBoundParameters) cats.Insert(tc);
                        }
                        if (cats.IsEmpty)
                        {
                            outcome.Skipped++;
                            outcome.Note($"\"{name}\": none of its categories exist in the target.");
                            continue;
                        }

                        RevitBinding newBinding = sb.Binding is InstanceBinding
                            ? (RevitBinding)app.Create.NewInstanceBinding(cats)
                            : app.Create.NewTypeBinding(cats);
                        ForgeTypeId groupId = sb.Definition.GetGroupTypeId();

                        if (_tgt.ParameterBindings.Insert(ext, newBinding, groupId)) outcome.Created++;
                        else if (_tgt.ParameterBindings.ReInsert(ext, newBinding, groupId)) outcome.Updated++;
                        else
                        {
                            outcome.Skipped++;
                            outcome.Note($"\"{name}\" could not be bound.");
                            continue;
                        }

                        if (sb.Binding is InstanceBinding)
                        {
                            Try(() =>
                            {
                                SharedParameterElement tspe = SharedParameterElement.Lookup(_tgt, sb.Shared.GuidValue);
                                InternalDefinition tdef = tspe?.GetDefinition();
                                if (tdef != null && tdef.VariesAcrossGroups != sb.Definition.VariesAcrossGroups)
                                    tdef.SetAllowVaryBetweenGroups(_tgt, sb.Definition.VariesAcrossGroups);
                            }, $"vary between groups for {name}");
                        }
                    }
                    catch (Exception ex)
                    {
                        outcome.Skipped++;
                        outcome.Note($"\"{name}\": {ex.Message}");
                        Debug.WriteLine($"{LogTag} project parameter \"{name}\" failed: {ex}");
                    }
                }
            }
            finally
            {
                // Put the user's shared parameter file back. If there was none, leave the temp file
                // in place so Revit is not pointed at a missing file.
                if (!string.IsNullOrEmpty(originalFile))
                {
                    Try(() => app.SharedParametersFilename = originalFile, "restore shared parameter file");
                    Try(() => File.Delete(tempFile), "delete temp shared parameter file");
                }
            }
            return outcome;
        }

        // ------------------------------------------------------------------
        // Revision settings
        // ------------------------------------------------------------------

        private TransferOutcome TransferRevisionSettings()
        {
            TransferOutcome outcome = new TransferOutcome();
            RevisionSettings s = RevisionSettings.GetRevisionSettings(_src);
            RevisionSettings t = RevisionSettings.GetRevisionSettings(_tgt);

            try
            {
                if (t.RevisionNumbering != s.RevisionNumbering) t.RevisionNumbering = s.RevisionNumbering;
                if (t.IsAcceptableRevisionCloudSpacing(s.RevisionCloudSpacing) && Math.Abs(t.RevisionCloudSpacing - s.RevisionCloudSpacing) > 1e-9)
                    t.RevisionCloudSpacing = s.RevisionCloudSpacing;
                outcome.Updated++;
            }
            catch (Exception ex)
            {
                outcome.Skipped++;
                outcome.Note($"Numbering / cloud spacing: {ex.Message}");
            }

            Dictionary<string, RevisionNumberingSequence> tgtSeqs = ByKey(
                RevisionNumberingSequence.GetAllRevisionNumberingSequences(_tgt).Select(id => _tgt.GetElement(id) as RevisionNumberingSequence).Where(x => x != null),
                x => x.SequenceName);

            foreach (ElementId id in RevisionNumberingSequence.GetAllRevisionNumberingSequences(_src))
            {
                RevisionNumberingSequence ss = _src.GetElement(id) as RevisionNumberingSequence;
                if (ss == null) continue;
                string name = ss.SequenceName;
                try
                {
                    bool numeric = ss.NumberType == RevisionNumberType.Numeric;
                    if (tgtSeqs.TryGetValue(name, out RevisionNumberingSequence ts))
                    {
                        if (ts.NumberType != ss.NumberType)
                        {
                            outcome.Skipped++;
                            outcome.Note($"Sequence \"{name}\" is {ss.NumberType} in the source but {ts.NumberType} in the target; left unchanged.");
                            continue;
                        }
                        if (numeric) ts.SetNumericRevisionSettings(ss.GetNumericRevisionSettings());
                        else ts.SetAlphanumericRevisionSettings(ss.GetAlphanumericRevisionSettings());
                        outcome.Updated++;
                    }
                    else
                    {
                        if (numeric) RevisionNumberingSequence.CreateNumericSequence(_tgt, name, ss.GetNumericRevisionSettings());
                        else RevisionNumberingSequence.CreateAlphanumericSequence(_tgt, name, ss.GetAlphanumericRevisionSettings());
                        outcome.Created++;
                    }
                }
                catch (Exception ex)
                {
                    outcome.Skipped++;
                    outcome.Note($"Sequence \"{name}\": {ex.Message}");
                }
            }
            return outcome;
        }
    }

    /// <summary>
    /// Swallows warnings raised while a standard is transferred and rolls the transaction
    /// back on errors, so Revit never pops a modal dialog in the middle of the run.
    /// </summary>
    internal class TransferFailuresPreprocessor : IFailuresPreprocessor
    {
        public List<string> Errors { get; } = new List<string>();

        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            bool hasError = false;
            foreach (FailureMessageAccessor failure in accessor.GetFailureMessages())
            {
                if (failure.GetSeverity() == FailureSeverity.Warning)
                {
                    accessor.DeleteWarning(failure);
                }
                else
                {
                    hasError = true;
                    Errors.Add(failure.GetDescriptionText());
                }
            }
            return hasError ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
        }
    }
}
