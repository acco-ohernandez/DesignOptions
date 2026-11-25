using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DesignOptions.Common
{
    /// <summary>
    /// Writes a plain-text inventory of every project standard the Transfer Project Standards
    /// command handles (plus the dependencies Revit drags along: line patterns, fill patterns,
    /// materials, tag types, revisions) for one or more documents. Values are in Revit internal
    /// units so a metric and an imperial model diff cleanly. Two snapshots of the same model,
    /// taken before and after a transfer, can be compared with any text diff tool.
    ///
    /// Files go to %LOCALAPPDATA%\Temp\Revit_ProjectStandards_Snapshots and only the newest
    /// <see cref="KeepFiles"/> are kept.
    /// </summary>
    internal static class ProjectStandardsSnapshot
    {
        private const string LogTag = "[StandardsSnapshot]";
        internal const int KeepFiles = 50;

        internal static string Folder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "Revit_ProjectStandards_Snapshots");

        /// <summary>
        /// Writes one snapshot file covering <paramref name="documents"/> and returns its path.
        /// <paramref name="label"/> is "before", "after" or "manual" and becomes part of the file name.
        /// </summary>
        internal static string Write(IEnumerable<Document> documents, Document target, string label)
        {
            List<Document> docs = documents.Where(d => d != null).Distinct().ToList();
            Directory.CreateDirectory(Folder);

            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string fileName = $"{Sanitize(target?.Title ?? "snapshot")}_{stamp}_{Sanitize(label)}.txt";
            string path = Path.Combine(Folder, fileName);

            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"SNAPSHOT {DateTime.Now:yyyy-MM-dd HH:mm:ss} LABEL: {label} TARGET: {target?.Title}");
            foreach (Document d in docs)
            {
                try { AppendDocument(sb, d); }
                catch (Exception ex)
                {
                    sb.AppendLine($"!! Snapshot of \"{d.Title}\" stopped: {ex.Message}");
                    Debug.WriteLine($"{LogTag} {d.Title}: {ex}");
                }
            }

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            Debug.WriteLine($"{LogTag} wrote {path} ({sb.Length} chars)");
            Prune();
            return path;
        }

        /// <summary>Deletes the oldest files so at most <see cref="KeepFiles"/> remain.</summary>
        internal static void Prune()
        {
            try
            {
                List<FileInfo> files = new DirectoryInfo(Folder).GetFiles("*.txt")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .ToList();
                foreach (FileInfo old in files.Skip(KeepFiles))
                {
                    try { old.Delete(); Debug.WriteLine($"{LogTag} pruned {old.Name}"); }
                    catch (Exception ex) { Debug.WriteLine($"{LogTag} could not delete {old.Name}: {ex.Message}"); }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"{LogTag} prune failed: {ex.Message}");
            }
        }

        private static string Sanitize(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "snapshot";
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Trim();
        }

        private static long IdValue(ElementId id)
        {
#if REVIT2024 || REVIT2025 || REVIT2026
            return id.Value;
#else
            return id.IntegerValue;
#endif
        }

        private static string SafeName(Element e)
        {
            try { return e?.Name ?? string.Empty; }
            catch { return string.Empty; }
        }

        // ------------------------------------------------------------------

        private static void AppendDocument(StringBuilder sb, Document d)
        {
            sb.AppendLine();
            sb.AppendLine($"#################### DOC: {d.Title} ({d.PathName})");

            string IdName(ElementId id)
            {
                if (id == null || id == ElementId.InvalidElementId) return "<none>";
                long v = IdValue(id);
                if (v < 0) return v == -3000010 ? "Solid" : "builtin:" + v;
                Element e = d.GetElement(id);
                return e == null ? "?" + v : SafeName(e);
            }

            string ParamLine(Element t)
            {
                List<string> parts = new List<string>();
                foreach (Parameter p in t.Parameters)
                {
                    if (p.IsReadOnly || !p.HasValue) continue;
                    string n = p.Definition.Name;
                    if (n == "Type IfcGUID" || n == "Type Image" || n == "Type Name" || n == "Family Name") continue;
                    string v;
                    switch (p.StorageType)
                    {
                        case StorageType.ElementId: v = IdName(p.AsElementId()); break;
                        case StorageType.Double: v = p.AsDouble().ToString("0.######"); break;
                        case StorageType.Integer: v = p.AsInteger().ToString(); break;
                        default: v = p.AsString(); break;
                    }
                    parts.Add(n + "=" + v);
                }
                return SafeName(t) + " {" + string.Join("; ", parts.OrderBy(x => x, StringComparer.Ordinal)) + "}";
            }

            void Section(string title, IEnumerable<string> names)
            {
                List<string> l = names.OrderBy(n => n, StringComparer.Ordinal).ToList();
                sb.AppendLine($"## {title} ({l.Count})");
                foreach (string n in l) sb.AppendLine("  " + n);
            }

            List<ElementType> types = new FilteredElementCollector(d).OfClass(typeof(ElementType)).Cast<ElementType>().ToList();
            Section("ElementType groups (class|family|category)", types
                .GroupBy(t => t.GetType().Name + " | " + (t.FamilyName ?? "") + " | " + (t.Category != null ? t.Category.Name : "-"))
                .Select(g => g.Key + " = " + g.Count()));

            Section("Arrowheads", types.Where(t => t.FamilyName == "Arrowhead").Select(ParamLine));
            Section("Elevation tag types", types.Where(t => t.FamilyName == "Elevation Tag").Select(ParamLine));
            Section("Section tag types", types.Where(t => t.FamilyName == "Section Tag").Select(ParamLine));
            Section("Callout tag types", types.Where(t => t.FamilyName == "Callout Tag").Select(ParamLine));
            Section("View reference types", types.Where(t => t.FamilyName == "View Reference").Select(ParamLine));
            Section("Viewport types", types.Where(t => t.FamilyName == "Viewport").Select(ParamLine));
            Section("Text types", types.OfType<TextNoteType>().Select(ParamLine));
            Section("Model text types", types.OfType<ModelTextType>().Select(ParamLine));
            Section("Dimension types", types.OfType<DimensionType>().Select(t =>
            {
                string units;
                try
                {
                    FormatOptions fo = t.GetUnitsFormatOptions();
                    units = " units{default=" + fo.UseDefault
                        + (fo.UseDefault ? "" : "; unit=" + fo.GetUnitTypeId().TypeId + "; acc=" + fo.Accuracy + "; symbol=" + (fo.GetSymbolTypeId() == null ? "" : fo.GetSymbolTypeId().TypeId))
                        + "}";
                }
                catch (Exception ex) { units = " units{ERR " + ex.Message + "}"; }
                return t.StyleType + " : " + ParamLine(t) + units;
            }));
            Section("Grid types", types.OfType<GridType>().Select(ParamLine));
            Section("Level types", types.OfType<LevelType>().Select(ParamLine));
            Section("Browser organizations", types.OfType<BrowserOrganization>()
                .Select(t => SafeName(t) + " [" + t.Type + "; sort=" + t.SortingOrder + "; sortParam=" + IdName(t.SortingParameterId) + "]"));
            Section("Line patterns", new FilteredElementCollector(d).OfClass(typeof(LinePatternElement)).Select(SafeName));
            Section("Fill patterns", new FilteredElementCollector(d).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                .Select(e => SafeName(e) + " [" + e.GetFillPattern().Target + "]"));
            Section("Materials", new FilteredElementCollector(d).OfClass(typeof(Material)).Select(SafeName));

            string CatLine(Category x)
            {
                try
                {
                    int? wpv = x.GetLineWeight(GraphicsStyleType.Projection);
                    string wp = wpv.HasValue ? wpv.Value.ToString() : "-";
                    string wc = "-"; try { int? wcv = x.GetLineWeight(GraphicsStyleType.Cut); wc = wcv.HasValue ? wcv.Value.ToString() : "-"; } catch { }
                    string pp = IdName(x.GetLinePatternId(GraphicsStyleType.Projection));
                    string pc = "-"; try { pc = IdName(x.GetLinePatternId(GraphicsStyleType.Cut)); } catch { }
                    string rgb = (x.LineColor != null && x.LineColor.IsValid) ? x.LineColor.Red + "," + x.LineColor.Green + "," + x.LineColor.Blue : "-";
                    string mat = "-"; try { mat = x.Material != null ? SafeName(x.Material) : "-"; } catch { }
                    return x.Name + " w=" + wp + "/" + wc + " rgb=" + rgb + " pat=" + pp + "/" + pc + " mat=" + mat;
                }
                catch (Exception ex) { return x.Name + " ERR " + ex.Message; }
            }

            Category lines = d.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
            Section("Line styles", lines.SubCategories.Cast<Category>().Select(CatLine));
            List<string> objStyles = new List<string>();
            foreach (Category c in d.Settings.Categories)
            {
                string kind = c.CategoryType.ToString();
                objStyles.Add("[" + kind + "] " + CatLine(c));
                foreach (Category s in c.SubCategories) objStyles.Add("[" + kind + "] " + c.Name + " > " + CatLine(s));
            }
            Section("Object styles", objStyles);

            Section("DWG export setups", new FilteredElementCollector(d).OfClass(typeof(ExportDWGSettings)).Cast<ExportDWGSettings>().Select(s =>
            {
                try
                {
                    DWGExportOptions o = s.GetDWGExportOptions();
                    ExportLayerTable lt = o.GetExportLayerTable();
                    return SafeName(s) + " {fileVersion=" + o.FileVersion + "; colors=" + o.Colors + "; layerMapping=" + o.LayerMapping
                        + "; layerKeys=" + lt.GetKeys().Count + "; propOverrides=" + o.PropOverrides + "; sharedCoords=" + o.SharedCoords
                        + "; mergedViews=" + o.MergedViews + "; exportOfSolids=" + o.ExportOfSolids + "; lineScaling=" + o.LineScaling
                        + "; textTreatment=" + o.TextTreatment + "; hideRefPlane=" + o.HideReferencePlane + "; hideScopeBox=" + o.HideScopeBox
                        + "; hideUnrefTags=" + o.HideUnreferenceViewTags + "; preserveCoincident=" + o.PreserveCoincidentLines
                        + "; hatchFile=" + o.HatchPatternsFileName + "}";
                }
                catch (Exception ex) { return SafeName(s) + " ERR " + ex.Message; }
            }));
            Section("Print settings", new FilteredElementCollector(d).OfClass(typeof(PrintSetting)).Cast<PrintSetting>().Select(p =>
            {
                try
                {
                    PrintParameters pp = p.PrintParameters;
                    string paper = "?"; try { paper = pp.PaperSize.Name; } catch { }
                    string src = "?"; try { src = pp.PaperSource.Name; } catch { }
                    return SafeName(p) + " {paper=" + paper + "; source=" + src + "; orient=" + pp.PageOrientation + "; placement=" + pp.PaperPlacement
                        + "; margin=" + pp.MarginType + "; offX=" + pp.OriginOffsetX.ToString("0.####") + "; offY=" + pp.OriginOffsetY.ToString("0.####")
                        + "; zoomType=" + pp.ZoomType + "; zoom=" + pp.Zoom + "; hidden=" + pp.HiddenLineViews + "; color=" + pp.ColorDepth
                        + "; raster=" + pp.RasterQuality + "; linksBlue=" + pp.ViewLinksinBlue + "; hideRef=" + pp.HideReforWorkPlanes
                        + "; hideUnref=" + pp.HideUnreferencedViewTags + "; hideScope=" + pp.HideScopeBoxes + "; hideCrop=" + pp.HideCropBoundaries
                        + "; halftone=" + pp.ReplaceHalftoneWithThinLines + "; mask=" + pp.MaskCoincidentLines + "}";
                }
                catch (Exception ex) { return SafeName(p) + " ERR " + ex.Message; }
            }));

            sb.AppendLine("## Phases (ordered)");
            int pi = 0;
            foreach (Phase p in d.Phases) sb.AppendLine("  " + (pi++) + ": " + SafeName(p));

            ElementOnPhaseStatus[] statuses = { ElementOnPhaseStatus.Existing, ElementOnPhaseStatus.Demolished, ElementOnPhaseStatus.New, ElementOnPhaseStatus.Temporary };
            Section("Phase filters", new FilteredElementCollector(d).OfClass(typeof(PhaseFilter)).Cast<PhaseFilter>()
                .Select(f => SafeName(f) + " [" + string.Join(",", statuses.Select(s => s + "=" + f.GetPhaseStatusPresentation(s))) + "]"));

            RevisionSettings rs = RevisionSettings.GetRevisionSettings(d);
            sb.AppendLine("## Revision settings: numbering=" + rs.RevisionNumbering + " cloudSpacing=" + rs.RevisionCloudSpacing.ToString("0.######"));
            Section("Revision sequences", RevisionNumberingSequence.GetAllRevisionNumberingSequences(d)
                .Select(id => d.GetElement(id) as RevisionNumberingSequence).Where(x => x != null).Select(x =>
                {
                    string s;
                    try
                    {
                        if (x.NumberType == RevisionNumberType.Numeric)
                        {
                            NumericRevisionSettings n = x.GetNumericRevisionSettings();
                            s = "start=" + n.StartNumber + "; minDigits=" + n.MinimumDigits + "; prefix=" + n.Prefix + "; suffix=" + n.Suffix;
                        }
                        else
                        {
                            AlphanumericRevisionSettings a = x.GetAlphanumericRevisionSettings();
                            s = "seq=" + string.Join("", a.GetSequence()) + "; prefix=" + a.Prefix + "; suffix=" + a.Suffix;
                        }
                    }
                    catch (Exception ex) { s = "ERR " + ex.Message; }
                    return x.SequenceName + " [" + x.NumberType + "; " + s + "]";
                }));
            Section("Revisions", new FilteredElementCollector(d).OfClass(typeof(Revision)).Cast<Revision>()
                .Select(r => r.SequenceNumber + ": " + r.RevisionNumber + " | " + r.Description + " | " + r.RevisionDate + " | " + r.Visibility + " | seq=" + IdName(r.RevisionNumberingSequenceId)));

            List<string> ppList = new List<string>();
            DefinitionBindingMapIterator it = d.ParameterBindings.ForwardIterator();
            it.Reset();
            while (it.MoveNext())
            {
                InternalDefinition def = it.Key as InternalDefinition;
                ElementBinding b = it.Current as ElementBinding;
                if (def == null) continue;
                SharedParameterElement spe = d.GetElement(def.Id) as SharedParameterElement;
                string grp;
                try { grp = LabelUtils.GetLabelForGroup(def.GetGroupTypeId()); }
                catch { grp = def.GetGroupTypeId() == null ? "" : def.GetGroupTypeId().TypeId; }
                string dt = ""; try { dt = def.GetDataType().TypeId; } catch { }
                string cats = b != null ? string.Join(",", b.Categories.Cast<Category>().Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal)) : "";
                ppList.Add(def.Name + " | " + (spe != null ? "shared " + spe.GuidValue : "NOT shared") + " | " + (b is InstanceBinding ? "instance" : "type")
                    + " | group=" + grp + " | type=" + dt + " | varies=" + def.VariesAcrossGroups + " | " + cats);
            }
            Section("Project parameters", ppList);
        }
    }
}
