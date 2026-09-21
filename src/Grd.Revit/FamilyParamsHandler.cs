using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using GrdRevit.Core;

namespace GrdRevit.Revit
{
    /// <summary>Результат применения операций с параметрами к семействам.</summary>
    public sealed class FamilyParamsResult
    {
        public List<string> Summary = new List<string>();
        public List<string> Errors = new List<string>();
        public int TotalFamilies;
        public int AppliedFamilies;
    }

    /// <summary>
    /// Применяет операции с параметрами (добавить/удалить) к выбранным семействам
    /// активного документа. Каждое семейство редактируется через Document
    /// (Document.EditFamily) и загружается обратно (LoadFamily) в отдельной транзакции.
    /// Общие параметры добавляются из указанного файла общих параметров (.txt).
    /// </summary>
    public class FamilyParamsHandler : IExternalEventHandler
    {
        private readonly object _sync = new object();
        private List<string> _familyNames;
        private bool _clearFormulaMode;
        private string _clearFormulaParam;
        private bool _verifyMode;
        private List<FamilyParamOp> _verifyOps;
        private Action<FamilyParamsResult> _callback;

        /// <summary>Сколько семейств подготавливать за одно событие ожидания: окно плагина
        /// успевает отрисоваться между событиями, показывая прогресс.</summary>
        private const int PrepareChunkSize = 3;

        /// <summary>Активный пакет операции «применить параметры»: обрабатывается порциями,
        /// чтобы между семействами окно плагина могло обновить прогресс-бар.</summary>
        private Batch _batch;

        private sealed class Batch
        {
            public List<string> Names = new List<string>();
            public List<FamilyParamOp> Ops = new List<FamilyParamOp>();
            public string SharedPath = string.Empty;
            public int Index;
            public bool Loading;
            public List<ReadyFamily> Pending = new List<ReadyFamily>();
            public FamilyParamsResult Result = new FamilyParamsResult();
            public Action<FamilyParamsResult, int> ProgressCb;
            public Action<FamilyParamsResult> DoneCb;
        }

        /// <summary>Очередь операции «применить параметры» к семействам. Выполняется поэтапно:
        /// после каждой порции семейств вызывается <paramref name="progress"/> (результат и
        /// индекс обработанных семейств), по завершении — <paramref name="done"/> с итогом.</summary>
        public void Queue(List<string> familyNames, List<FamilyParamOp> ops, string sharedFilePath,
            Action<FamilyParamsResult, int> progress, Action<FamilyParamsResult> done)
        {
            lock (_sync)
            {
                var names = familyNames ?? new List<string>();
                _batch = new Batch
                {
                    Names = names,
                    Ops = ops ?? new List<FamilyParamOp>(),
                    SharedPath = sharedFilePath ?? string.Empty,
                    Result = new FamilyParamsResult { TotalFamilies = names.Count },
                    ProgressCb = progress,
                    DoneCb = done
                };
            }
        }

        /// <summary>Очередь операции «снять формулу»: у указанного параметра убирается
        /// формула во всех отмеченных семействах (все типы), затем семейства
        /// перезагружаются в проект.</summary>
        public void QueueClearFormula(List<string> familyNames, string paramName, Action<FamilyParamsResult> callback)
        {
            lock (_sync)
            {
                _familyNames = familyNames ?? new List<string>();
                _clearFormulaMode = true;
                _clearFormulaParam = paramName ?? string.Empty;
                _verifyMode = false;
                _verifyOps = null;
                _callback = callback;
            }
        }

        /// <summary>Очередь проверки: фактически сверяет по каждому отмеченному семейству,
        /// есть ли в нём каждый параметр из <paramref name="ops"/> (в диспетчере параметров
        /// и на типе в проекте). Работает без изменения документов — только чтение.
        /// Нужно, чтобы отличить «параметр не добавился» от «добавился, но не показан
        /// в списке» (список при нескольких семействах показывает лишь общие для всех).</summary>
        public void QueueVerify(List<string> familyNames, List<FamilyParamOp> ops, Action<FamilyParamsResult> callback)
        {
            lock (_sync)
            {
                _familyNames = familyNames ?? new List<string>();
                _clearFormulaMode = false;
                _clearFormulaParam = null;
                _verifyMode = true;
                _verifyOps = ops ?? new List<FamilyParamOp>();
                _callback = callback;
            }
        }

        public string GetName()
        {
            return "JTOOLS: изменить параметры семейств";
        }

        public void Execute(UIApplication app)
        {
            Batch batch;
            lock (_sync) { batch = _batch; }
            if (batch != null)
            {
                try { ExecuteBatch(app, batch); }
                catch (Exception ex)
                {
                    GrdLog.Log("FamilyParamsHandler.ExecuteBatch: EXCEPTION: " + ex);
                    batch.Result.Errors.Add("Ошибка: " + ex.Message);
                    FinalizeBatch(batch);
                }
                return;
            }
            ExecuteSingleShot(app);
        }

        /// <summary>Поэтапное выполнение операции «применить параметры»: по PrepareChunkSize
        /// семейств за событие (пока окно плагина успевает рисовать прогресс между событиями),
        /// после подготовки всех семейств — пакетная перезагрузка одной транзакцией.</summary>
        private void ExecuteBatch(UIApplication app, Batch b)
        {
            var uiDoc = app?.ActiveUIDocument;
            var doc = uiDoc?.Document;

            if (!b.Loading && !string.IsNullOrEmpty(b.SharedPath) && System.IO.File.Exists(b.SharedPath))
            {
                // Файл общих параметров для сеанса: указываем заранее, чтобы общие параметры
                // добавлялись из него, а новые определения попадали в правильный файл.
                try { app.Application.SharedParametersFilename = b.SharedPath; }
                catch (Exception ex)
                {
                    // Файл уже задан или недоступен — продолжаем, Revit сам разберётся.
                    GrdLog.Log("FamilyParamsHandler: SharedParametersFilename: " + ex.Message);
                }
            }

            if (b.Loading)
            {
                RunLoadPhase(app, b);
                return;
            }

            int done = 0;
            while (b.Index < b.Names.Count && done < PrepareChunkSize)
            {
                var familyName = b.Names[b.Index++];
                done++;
                string line;
                try
                {
                    line = doc == null
                        ? "! Нет активного документа Revit."
                        : PrepareFamily(doc, app, familyName, b.Ops, b.Pending);
                }
                catch (Exception ex)
                {
                    GrdLog.Log("FamilyParamsHandler: «" + familyName + "» EXCEPTION: " + ex);
                    line = "! «" + familyName + "»: " + ex.Message;
                }
                if (line == null) continue;
                if (line.StartsWith("!")) b.Result.Errors.Add(line);
                else { b.Result.AppliedFamilies++; b.Result.Summary.Add(line); }
            }

            NotifyProgress(b, b.Index);
            if (b.Index >= b.Names.Count)
            {
                // Подготовка всех семейств завершена — переходим к пакетной перезагрузке.
                b.Loading = true;
                NotifyProgress(b, b.Index);
            }
            Raise();
        }

        private void NotifyProgress(Batch b, int done)
        {
            try { b.ProgressCb?.Invoke(b.Result, done); }
            catch (Exception ex) { GrdLog.Log("FamilyParamsHandler: progress EXCEPTION: " + ex); }
        }

        private void RunLoadPhase(UIApplication app, Batch b)
        {
            var doc = app?.ActiveUIDocument?.Document;
            var problems = new ProblemCollector();
            try
            {
                if (doc == null)
                {
                    b.Result.Errors.Add("Нет активного документа Revit.");
                }
                else if (b.Pending.Count > 0)
                {
                    LoadBatch(doc, b.Pending, b.Result, problems);
                }
            }
            catch (Exception ex)
            {
                GrdLog.Log("FamilyParamsHandler.RunLoadPhase: EXCEPTION: " + ex);
                b.Result.Errors.Add("Ошибка: " + ex.Message);
            }

            // Пропущенные («вопросные») семейства — отдельной спецификацией и сразу открыть.
            if (problems.FamilyNames.Count > 0)
            {
                CreateProblemSchedule(app, doc, problems);
            }
            FinalizeBatch(b);
        }

        private void FinalizeBatch(Batch b)
        {
            lock (_sync) { if (ReferenceEquals(_batch, b)) _batch = null; }
            GrdLog.Log("FamilyParamsHandler: применено " + b.Result.AppliedFamilies + " из " +
                       b.Result.TotalFamilies + ", ошибок " + b.Result.Errors.Count);
            try { b.DoneCb?.Invoke(b.Result); }
            catch (Exception ex) { GrdLog.Log("FamilyParamsHandler: done EXCEPTION: " + ex); }
        }

        /// <summary>Запланировать продолжение обработки и дать окну плагина отрисоваться.</summary>
        private void Raise()
        {
            try { RevitContext.FamilyParamsEvent?.Raise(); }
            catch (Exception ex) { GrdLog.Log("FamilyParamsHandler.Raise EXCEPTION: " + ex); }
        }

        /// <summary>Разовая обработка одношаговых операций: «снять формулу» и «проверить».</summary>
        private void ExecuteSingleShot(UIApplication app)
        {
            List<string> names;
            bool clearFormula;
            string clearParam;
            bool verify;
            List<FamilyParamOp> verifyOps;
            Action<FamilyParamsResult> cb;
            lock (_sync)
            {
                names = _familyNames;
                clearFormula = _clearFormulaMode;
                clearParam = _clearFormulaParam;
                verify = _verifyMode;
                verifyOps = _verifyOps;
                cb = _callback;
                _familyNames = null;
                _clearFormulaMode = false;
                _clearFormulaParam = null;
                _verifyMode = false;
                _verifyOps = null;
                _callback = null;
            }
            if (cb == null)
            {
                GrdLog.Log("FamilyParamsHandler.Execute: нет запроса в очереди");
                return;
            }

            var result = new FamilyParamsResult();
            try
            {
                result = verify
                    ? ApplyVerify(app, names, verifyOps)
                    : clearFormula
                        ? ApplyClearFormula(app, names, clearParam)
                        : new FamilyParamsResult { TotalFamilies = names == null ? 0 : names.Count };
            }
            catch (Exception ex)
            {
                GrdLog.Log("FamilyParamsHandler.Execute: EXCEPTION: " + ex);
                result.Errors.Add("Ошибка: " + ex.Message);
            }

            GrdLog.Log("FamilyParamsHandler: применено " + result.AppliedFamilies + " из " + result.TotalFamilies +
                       ", ошибок " + result.Errors.Count);
            try { cb(result); }
            catch (Exception ex) { GrdLog.Log("FamilyParamsHandler.Execute: callback EXCEPTION: " + ex); }
        }

/// <summary>
/// Подготавливает одно семейство: проверяет, не покрыт ли общий параметр привязкой
/// в проекте (BindingMap), при необходимости редактирует документ семейства и
/// сохраняет его во временный файл, добавляя в <paramref name="pending"/> для
/// пакетной перезагрузки. Возвращает строку отчёта (ошибки — с «!»), либо null,
/// если семейство ушло на перезагрузку.
/// </summary>
private static string PrepareFamily(Document doc, UIApplication app, string familyName,
    List<FamilyParamOp> ops, List<ReadyFamily> pending)
{
    Family family = null;
    foreach (Element e in new FilteredElementCollector(doc).OfClass(typeof(Family)))
    {
        var f = e as Family;
        if (f == null || string.IsNullOrEmpty(f.Name)) continue;
        if (string.Equals(f.Name, familyName, StringComparison.OrdinalIgnoreCase))
        {
            family = f;
            break;
        }
    }
    if (family == null) return "! Семейство «" + familyName + "» не найдено в документе.";

    // Быстрый путь: общий параметр со статусом «Добавить» уже привязан к категории
    // семейства в проекте (BindingMap) тем же способом (тип/экземпляр) — элементы
    // семейства его уже имеют, открывать и перезагружать семейство не нужно.
    var projectCovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var op in ops)
    {
        if (op == null || string.IsNullOrEmpty(op.Name)) continue;
        if (AlreadyCoveredByProjectBinding(doc, family, op)) projectCovered.Add(op.Name);
    }
    var workOps = ops
        .Where(o => o != null && !string.IsNullOrEmpty(o.Name) && !projectCovered.Contains(o.Name))
        .ToList();
    if (workOps.Count == 0)
        return "«" + familyName + "»: уже привязано в проекте: " + string.Join(", ", projectCovered);

    Document fdoc;
    try { fdoc = doc.EditFamily(family); }
    catch (Exception ex)
    {
        return "! «" + familyName + "»: не удалось открыть семейство: " + ex.Message;
    }

    var ready = new ReadyFamily { FamilyName = familyName };
    foreach (var n in projectCovered) ready.Already.Add(n + " (привязка в проекте)");

    var added = ready.Added;
    var removed = ready.Removed;
    var skipped = ready.Skipped;
    var already = ready.Already;
    var rebound = ready.Rebound;
    var reboundNames = ready.ReboundNamesSet;
    var bindSnapshots = ready.BindSnapshots;
    var valueSnapshots = ready.ValueSnapshots;
    string tempPath = null;
    int sameName = 0;
    try
    {
        // Параметры, привязанные к категории в ПРОЕКТЕ, но не встроенные
        // в семейство, в FamilyManager отсутствуют — «экземпляр ↔ тип» для
        // них выполняется перепривязкой в проекте (ReInsert). Делаем это
        // отдельной транзакцией проекта ДО правок семейного файла.
        try
        {
            bool anyProjectRebind = false;
            foreach (var op in workOps)
            {
                if (op == null || string.IsNullOrEmpty(op.Name)) continue;
                if (!string.Equals(op.Status, "Изменить привязку", StringComparison.OrdinalIgnoreCase)) continue;
                if (FindParam(fdoc.FamilyManager, op) != null) continue;
                anyProjectRebind = true;
                break;
            }
            if (anyProjectRebind)
            {
                using (var pt = new Transaction(doc, "JTOOLS: смена привязки параметров (проект)"))
                {
                    pt.Start();
                    foreach (var op in workOps)
                    {
                        if (op == null || string.IsNullOrEmpty(op.Name)) continue;
                        if (!string.Equals(op.Status, "Изменить привязку", StringComparison.OrdinalIgnoreCase)) continue;
                        if (FindParam(fdoc.FamilyManager, op) != null) continue;
                        if (TryProjectRebind(doc, family, op))
                        {
                            rebound.Add(op.Name + " (" + (op.IsInstance ? "тип→экземпляр" : "экземпляр→тип") + ")");
                            reboundNames.Add(op.Name);
                        }
                    }
                    pt.Commit();
                }
            }
        }
        catch (Exception ex)
        {
            GrdLog.Log("FamilyParamsHandler: проектная перепривязка EXCEPTION: " + ex);
        }

        // Изменение параметров в документе семейства ОБЯЗАНО идти внутри
        // транзакции этого документа. Иначе AddParameter/RemoveParameter/
        // MakeInstance оставляют «висячие» правки: сводка показывает «добавлено»,
        // но SaveAs их не фиксирует, и после LoadFamily параметра в семействе нет.
        using (var ft = new Transaction(fdoc, "JTOOLS: параметры «" + familyName + "»"))
        {
            ft.Start();
            try
            {
                foreach (var op in workOps)
                {
                    if (op == null || string.IsNullOrEmpty(op.Name)) continue;
                    if (reboundNames.Contains(op.Name)) continue;
                    try
                    {
                        ApplyOp(fdoc, app, op, added, removed, skipped, already);
                    }
                    catch (Exception ex)
                    {
                        skipped.Add(op.Name + " («" + ex.Message + "»)");
                    }
                }
            }
            catch
            {
                try { ft.RollBack(); } catch { }
                throw;
            }
            ft.Commit();
        }

        GrdLog.Log("FamilyParamsHandler: после правок в семействе «" + familyName +
                   "» параметров=" + fdoc.FamilyManager?.GetParameters().Count +
                   ", OwnerFamily=" + (fdoc.OwnerFamily?.Name ?? "<null>") +
                   ", PathName=" + (fdoc.PathName ?? "<н/з>"));

        // Если в семействе ничего не изменилось (параметры в нём уже были —
        // добавление сработало только для других семейств), перезагружать его
        // не нужно: перезагрузка вхолостую может затронуть значения параметров.
        if (added.Count == 0 && removed.Count == 0)
        {
            var nb = new List<string>();
            if (already.Count > 0) nb.Add("уже есть: " + string.Join(", ", already));
            if (skipped.Count > 0) nb.Add("пропущено: " + string.Join(", ", skipped));
            if (rebound.Count > 0) nb.Add("смена привязки (проект): " + string.Join(", ", rebound));
            GrdLog.Log("FamilyParamsHandler: «" + familyName + "» без изменений — без перезагрузки");
            return "«" + familyName + "»: " + (nb.Count > 0 ? string.Join("; ", nb) : "нет изменений");
        }

        // Снимаем текущие значения параметров со сменой привязки «экземпляр ↔ тип»:
        // после LoadFamily Revit обнуляет их, и мы вернём их обратно (RestoreBinding).
        // Момент важен: правки сделаны только в документе семейства, проект не тронут.
        foreach (var op in workOps)
        {
            if (op == null || string.IsNullOrEmpty(op.Name)) continue;
            if (!string.Equals(op.Status, "Изменить привязку", StringComparison.OrdinalIgnoreCase)) continue;
            if (reboundNames.Contains(op.Name)) continue;
            try
            {
                bindSnapshots.Add(CaptureBinding(doc, familyName, op.Name, wasInstance: !op.IsInstance));
            }
            catch (Exception ex)
            {
                GrdLog.Log("FamilyParamsHandler: capture «" + op.Name + "» EXCEPTION: " + ex);
            }
        }

        // «Перенести в семейство» / добавление общего параметра, который СЕЙЧАС
        // привязан к проекту: значения параметра лежат на экземплярах (или типах)
        // в документе. Сохраняем их ДО перезагрузки семейства, после LoadFamily
        // возвращаем обратно (иначе значения обнуляются).
        foreach (var op in workOps)
        {
            if (op == null || string.IsNullOrEmpty(op.Name) || op.Remove) continue;
            if (op.Source != ParamSourceKind.Shared) continue;
            if (!string.Equals(op.Status, "Добавить", StringComparison.OrdinalIgnoreCase)) continue;
            var binding = ProjectBindingFor(doc, family, op);
            if (binding == null) continue;
            valueSnapshots.AddRange(CaptureValues(doc, family, op.Name, binding));
        }

        // Дубли семейства: если в проекте уже есть НЕСКОЛЬКО семейств с одним
        // именем, LoadFamily предсказуемо обновить не может тот или иной —
        // параметр «то появляется, то нет» (см. журналы). Предупреждаем заранее.
        foreach (Element e in new FilteredElementCollector(doc).OfClass(typeof(Family)))
        {
            var f = e as Family;
            if (f != null && !string.IsNullOrEmpty(f.Name) &&
                string.Equals(f.Name, family.Name, StringComparison.OrdinalIgnoreCase)) sameName++;
        }
        if (sameName > 1)
            GrdLog.Log("FamilyParamsHandler: ВНИМАНИЕ: в проекте " + sameName +
                       " семейств с именем «" + family.Name + "» — LoadFamily обновит одно из них," +
                       " остальное(ые) останется без изменений. Рекомендуется удалить дубль.");

        // LoadFamily(Document, options) нельзя вызывать на модифицированном
        // документе семейства («The document must not be modifiable...»).
        // Поэтому сохраняем семейство во временный файл и загружаем по пути.
        // ВАЖНО: имя временного файла должно максимально совпадать с тем,
        // из какого файла семейство попало в проект (fdoc.PathName), иначе
        // Revit нагрузит семейство «как новое» по имени RFA-файла и создаст
        // семейство-дубль вместо замены существующего (см. Audytor_<guid>).
        tempPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            FamilyReloadFileName(fdoc, familyName));
        if (System.IO.File.Exists(tempPath)) System.IO.File.Delete(tempPath);
        fdoc.SaveAs(tempPath, new SaveAsOptions());
        GrdLog.Log("FamilyParamsHandler: «" + familyName + "» сохранено во временный файл " + tempPath);
    }
    catch (Exception ex)
    {
        GrdLog.Log("FamilyParamsHandler: «" + familyName + "» EXCEPTION: " + ex);
        return "! «" + familyName + "»: " + ex.Message;
    }
    finally
    {
        try { fdoc.Dispose(); } catch { }
    }

    ready.TempPath = tempPath;
    ready.SameName = sameName;
    pending.Add(ready);
    return null;
}

/// <summary>Список семейств, пропущенных при перезагрузке (см. CreateProblemSchedule).</summary>
private sealed class ProblemCollector
{
    public readonly HashSet<string> FamilyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Контекст перехвата сбоев перезагрузки: «вопросные» сбои (например,
/// «Remove constraints») гасятся молча, чтобы не прерывать операцию диалогом.</summary>
private sealed class LoadFailuresContext
{
    public bool SuppressedConstraint;
}

/// <summary>Перехватывает сбои транзакции LoadFamily: «вопросные» сбои (которые иначе
/// показали бы нативный диалог Revit) преобразуются в тихий откат транзакции; обычные
/// предупреждения пропускаются как есть (Continue — поведение Revit по умолчанию).</summary>
private sealed class LoadFailuresPreprocessor : IFailuresPreprocessor
{
    private readonly LoadFailuresContext _ctx;
    public LoadFailuresPreprocessor(LoadFailuresContext ctx) { _ctx = ctx; }

    public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
    {
        if (_ctx.SuppressedConstraint) return FailureProcessingResult.Continue;
        foreach (var msg in failuresAccessor.GetFailureMessages())
        {
            if (IsConstraintStyle(msg))
            {
                _ctx.SuppressedConstraint = true;
                break;
            }
        }
        // ProceedWithRollBack не показывает никаких диалогов — транзакция просто
        // откатится, а операция повторит перезагрузку по одному семейству.
        return _ctx.SuppressedConstraint ? FailureProcessingResult.ProceedWithRollBack
                                         : FailureProcessingResult.Continue;
    }

    /// <summary>«Вопросный» сбой: перезагрузка требует вмешательства пользователя
    /// («Remove constraints», отсоединение и т.п.). Опознаём по тексту описания —
    /// заголовки таких диалогов остаются английскими даже в локализованном Revit,
    /// поэтому ищем и английские, и русские корни.</summary>
    private static bool IsConstraintStyle(FailureMessageAccessor fm)
    {
        try
        {
            var d = (fm.GetDescriptionText() ?? string.Empty) + " " +
                    (fm.GetDefaultResolutionCaption() ?? string.Empty);
            d = d.ToLowerInvariant();
            return d.Contains("constraint")
                || d.Contains("ограничен")
                || d.Contains("разъед")
                || d.Contains("unjoin")
                || d.Contains("disconnect");
        }
        catch { return false; }
    }
}

/// <summary>
/// Пакетная перезагрузка подготовленных семейств: LoadFamily всех (плюс
/// восстановление значений) выполняется в ОДНОЙ транзакции проекта — вместо
/// отдельной транзакции на каждое семейство. При любом сбое транзакция целиком
/// откатывается и каждое семейство перезагружается отдельно (изоляция ошибок).
/// «Вопросные» сбои («Remove constraints» и т.п.) гасятся молча: такие семейства
/// тихо пропускаются и собираются в <paramref name="problems"/> для отдельной
/// спецификации.
/// </summary>
private static void LoadBatch(Document doc, List<ReadyFamily> pending, FamilyParamsResult result,
    ProblemCollector problems)
{
    if (pending == null || pending.Count == 0) return;
    var beforeNames = FamilyNames(doc);
    bool anyLoaded = false;
    bool needRollback = false;
    var ctx = new LoadFailuresContext();

    using (var t = new Transaction(doc, "JTOOLS: параметры семейств (загрузка)"))
    {
        try
        {
            var opts = t.GetFailureHandlingOptions();
            opts.SetFailuresPreprocessor(new LoadFailuresPreprocessor(ctx));
            t.SetFailureHandlingOptions(opts);
        }
        catch (Exception ex) { GrdLog.Log("LoadBatch: FailureHandlingOptions: " + ex.Message); }
        t.Start();
        try
        {
            foreach (var f in pending)
            {
                try
                {
                    bool loaded = doc.LoadFamily(f.TempPath, new FamilyLoadOptionsImpl(), out var loadedFamily);
                    if (!loaded)
                    {
                        f.Error = "LoadFamily вернул false";
                        continue;
                    }
                    f.LoadedFamilyName = loadedFamily?.Name ?? string.Empty;
                    anyLoaded = true;
                    foreach (var snap in f.BindSnapshots) RestoreBinding(doc, f.FamilyName, snap);
                    RestoreValues(doc, f.FamilyName, f.ValueSnapshots);
                }
                catch (Exception ex)
                {
                    // Исключение означает частичную правку у этого семейства — откатим
                    // всю пакетную транзакцию и повторим каждое семейство отдельно.
                    needRollback = true;
                    f.Error = ex.Message;
                    GrdLog.Log("LoadBatch «" + f.FamilyName + "» EXCEPTION: " + ex);
                }
            }
        }
        catch (Exception ex)
        {
            needRollback = true;
            GrdLog.Log("LoadBatch EXCEPTION: " + ex);
        }
        finally
        {
            try
            {
                if (anyLoaded && !needRollback)
                {
                    if (t.Commit() != TransactionStatus.Committed) needRollback = true;
                }
                else t.RollBack();
            }
            catch { needRollback = true; }
        }
    }

    if (needRollback)
    {
        // Пакет откачен целиком — каждое семейство в собственной транзакции.
        // «Вопросные» семейства здесь тихо пропускаются (без диалогов).
        foreach (var f in pending)
        {
            var line = LoadFamilySingle(doc, f, beforeNames, result, problems);
            if (line == null) continue;
            if (line.StartsWith("!")) result.Errors.Add(line);
            else { result.AppliedFamilies++; result.Summary.Add(line); }
        }
        return;
    }

    DeleteTempFiles(pending);

    foreach (var f in pending)
    {
        if (f.Error != null)
        {
            result.Errors.Add("! «" + f.FamilyName + "»: " + f.Error);
            continue;
        }
        VerifyAfterLoad(doc, f.FamilyName, beforeNames, f.Added);
        result.AppliedFamilies++;
        result.Summary.Add(BuildFamilyLine(f));
    }
}

/// <summary>Перезагружает одно семейство в собственной транзакции (путь отката
/// пакетной нагрузки при сбое / изоляция ошибки конкретного семейства).
/// «Вопросные» сбои («Remove constraints» и т.п.) гасятся молча: семейство
/// в проекте не меняется, добавляется в <paramref name="problems"/> и получает
/// строку-отчёт (возвращается null — применённым не считается).</summary>
private static string LoadFamilySingle(Document doc, ReadyFamily f, List<string> beforeNames,
    FamilyParamsResult result, ProblemCollector problems)
{
    bool loaded = false;
    string tempPath = f.TempPath;
    var ctx = new LoadFailuresContext();
    try
    {
        using (var t = new Transaction(doc, "JTOOLS: параметры семейства «" + f.FamilyName + "»"))
        {
            try
            {
                var opts = t.GetFailureHandlingOptions();
                opts.SetFailuresPreprocessor(new LoadFailuresPreprocessor(ctx));
                t.SetFailureHandlingOptions(opts);
            }
            catch (Exception ex) { GrdLog.Log("LoadFamilySingle: FailureHandlingOptions: " + ex.Message); }
            t.Start();
            try
            {
                loaded = doc.LoadFamily(tempPath, new FamilyLoadOptionsImpl(), out var loadedFamily);
                if (loaded)
                {
                    f.LoadedFamilyName = loadedFamily?.Name ?? string.Empty;
                    foreach (var snap in f.BindSnapshots) RestoreBinding(doc, f.FamilyName, snap);
                    RestoreValues(doc, f.FamilyName, f.ValueSnapshots);
                }
                t.Commit();
            }
            catch
            {
                try { t.RollBack(); } catch { }
                throw;
            }
        }
        if (ctx.SuppressedConstraint)
        {
            // Перезагрузка потребовала бы диалога Revit («Remove constraints») —
            // тихо пропускаем: семейство в проекте остаётся прежним, а его экземпляры
            // попадают в отдельную спецификацию для ручного разбора.
            problems?.FamilyNames.Add(f.FamilyName);
            result?.Summary.Add("«" + f.FamilyName + "»: ПРОПУЩЕНО (перезагрузка требует действий Revit) — " +
                "список — в спецификации «JTOOLS: перезагрузка…»");
            return null;
        }
        if (!loaded) return "! «" + f.FamilyName + "»: LoadFamily вернул false";
        VerifyAfterLoad(doc, f.FamilyName, beforeNames, f.Added);
        return BuildFamilyLine(f);
    }
    catch (Exception ex)
    {
        GrdLog.Log("FamilyParamsHandler: перезагрузка «" + f.FamilyName + "» EXCEPTION: " + ex);
        return "! «" + f.FamilyName + "»: " + ex.Message;
    }
    finally
    {
        DeleteTempFile(tempPath);
    }
}

/// <summary>Создаёт спецификацию с семействами, пропущенными при перезагрузке, и
/// открывает её. Параметр-метка («JTOOLS: перезагрузка — пропущено») создаётся
/// плагином как общий параметр экземпляра и привязывается к категориям проблемных
/// семейств; по нему спецификации фильтруются штатными средствами Revit
/// (Properties → «Фильтры» пользователь увидит этот параметр).</summary>
private static void CreateProblemSchedule(UIApplication app, Document doc, ProblemCollector problems)
{
    try
    {
        if (app == null || doc == null || problems == null || problems.FamilyNames.Count == 0) return;

        // Категории проблемных семейств — для привязки параметра и создания таблиц.
        var cats = new SortedDictionary<long, Category>();
        foreach (Element e in new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)))
        {
            var sym = e as FamilySymbol;
            if (sym?.Family == null || string.IsNullOrEmpty(sym.Family.Name)) continue;
            if (!problems.FamilyNames.Contains(sym.Family.Name)) continue;
            if (sym.Category != null) cats[sym.Category.Id.Value] = sym.Category;
        }
        if (cats.Count == 0)
        {
            GrdLog.Log("CreateProblemSchedule: категорий проблемных семейств не найдено");
            return;
        }

        var catSet = doc.Application.Create.NewCategorySet();
        foreach (var c in cats.Values) catSet.Insert(c);

        using (var t = new Transaction(doc, "JTOOLS: параметр-метка и спецификация перезагрузки"))
        {
            t.Start();
            var def = EnsureMarkerDefinition(app, doc, catSet);
            if (def == null)
            {
                t.RollBack();
                return;
            }

            // Ставим значение-метку на экземпляры проблемных семейств, чтобы фильтр
            // штатной спецификации отобрал ровно их; попутно берём id параметра.
            ElementId markerId = ElementId.InvalidElementId;
            foreach (Element e in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)))
            {
                var inst = e as FamilyInstance;
                if (inst?.Symbol?.Family == null || string.IsNullOrEmpty(inst.Symbol.Family.Name)) continue;
                if (!problems.FamilyNames.Contains(inst.Symbol.Family.Name)) continue;
                var p = inst.LookupParameter(def.Name);
                if (p == null) continue;
                try { p.Set(1.0); }
                catch (Exception ex) { GrdLog.Log("CreateProblemSchedule: значение-метка: " + ex.Message); }
                if (markerId == ElementId.InvalidElementId) markerId = p.Id;
            }
            if (markerId == ElementId.InvalidElementId)
            {
                // Экземпляров проблемных семейств в проекте нет — привязку оставляем,
                // спецификацию не создаём (фильтровать было бы нечего).
                GrdLog.Log("CreateProblemSchedule: экземпляров проблемных семейств не найдено, " +
                           "спецификация не создана (параметр «" + def.Name + "» привязан)");
                t.Commit();
                return;
            }

            // Спецификация на каждую категорию (имя — случайное), фильтр по параметру-метке.
            var baseName = "JTOOLS: перезагрузка «пропущено» " +
                Guid.NewGuid().ToString("N").Substring(0, 5).ToUpperInvariant();
            ViewSchedule first = null;
            foreach (var c in cats.Values)
            {
                try
                {
                    var sched = ViewSchedule.CreateSchedule(doc, c.Id);
                    sched.Name = baseName + " — " + c.Name;
                    var field = sched.Definition.AddField(ScheduleFieldType.Instance, markerId);
                    sched.Definition.AddFilter(new ScheduleFilter(field.FieldId, ScheduleFilterType.Equal, 1.0));
                    if (first == null) first = sched;
                }
                catch (Exception ex)
                {
                    GrdLog.Log("CreateProblemSchedule: спецификация «" + c.Name + "» EXCEPTION: " + ex.Message);
                }
            }

            if (t.Commit() != TransactionStatus.Committed) t.RollBack();

            if (first != null && first.IsValidObject)
            {
                // Сразу открываем спецификацию для разбора пропущенных семейств.
                try { app.ActiveUIDocument?.RequestViewChange(first); }
                catch (Exception ex) { GrdLog.Log("CreateProblemSchedule: RequestViewChange: " + ex.Message); }
            }
        }

        GrdLog.Log("CreateProblemSchedule: пропущены: " + string.Join("; ", problems.FamilyNames));
    }
    catch (Exception ex)
    {
        GrdLog.Log("CreateProblemSchedule EXCEPTION: " + ex);
    }
}

/// <summary>Гарантирует наличие общего параметра-метки и его привязки к
/// <paramref name="catSet"/>. Возвращает определение параметра (null при неудаче).
/// Если параметр уже создан ранее и привязка покрывает нужные категории —
/// переиспользуется, иначе создаётся новое определение «по внутренней логике плагина».</summary>
private static ExternalDefinition EnsureMarkerDefinition(UIApplication app, Document doc, CategorySet catSet)
{
    try
    {
        const string groupName = "JTOOLS";
        const string baseName = "JTOOLS: перезагрузка — пропущено";

        var file = OpenOrCreateSharedFile(app);
        if (file == null) return null;

        var group = file.Groups.get_Item(groupName) ?? file.Groups.Create(groupName);

        var existing = group.Definitions.get_Item(baseName) as ExternalDefinition;
        if (existing != null)
        {
            var binding = doc.ParameterBindings.get_Item(existing) as InstanceBinding;
            if (binding != null)
            {
                if (CategoriesCovered(binding, catSet)) return existing;
                // Определение уже привязано, но не ко всем нужным категориям —
                // ниже создаём дополнительное определение со своим именем.
            }
            else
            {
                // Определение есть, но не привязано в проекте — привязываем.
                doc.ParameterBindings.Insert(existing, new InstanceBinding(catSet));
                return existing;
            }
        }

        // Создаём определение (число-метку), с гарантией уникальности имени.
        string name = baseName;
        int n = 2;
        while (group.Definitions.get_Item(name) != null) name = baseName + " (" + (n++) + ")";
        var def = group.Definitions.Create(new ExternalDefinitionCreationOptions(name, SpecTypeId.Number)
        {
            Description = "JTOOLS: семейства, пропущенные при пакетной перезагрузке параметров"
        }) as ExternalDefinition;
        if (def == null) return null;

        doc.ParameterBindings.Insert(def, new InstanceBinding(catSet));
        return def;
    }
    catch (Exception ex)
    {
        GrdLog.Log("EnsureMarkerDefinition EXCEPTION: " + ex);
        return null;
    }
}

/// <summary>True, если привязка <paramref name="binding"/> покрывает все категории
/// из <paramref name="needed"/>.</summary>
private static bool CategoriesCovered(InstanceBinding binding, CategorySet needed)
{
    try
    {
        if (binding == null || needed == null) return false;
        foreach (Category c in needed)
        {
            bool found = false;
            foreach (Category bc in binding.Categories)
            {
                if (bc.Id == c.Id) { found = true; break; }
            }
            if (!found) return false;
        }
        return true;
    }
    catch { return false; }
}

/// <summary>Возвращает файл общих параметров для параметра-метки: использует файл
/// текущего сеанса (Settings.LastSharedParamsPath), при отсутствии — создаёт свой
/// во временной папке.</summary>
private static DefinitionFile OpenOrCreateSharedFile(UIApplication app)
{
    try
    {
        string path = app.Application.SharedParametersFilename;
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            path = RevitContext.Settings.LastSharedParamsPath;
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
        {
            path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "JTOOLS_SharedParams_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".txt");
            try { System.IO.File.WriteAllBytes(path, System.Text.Encoding.UTF8.GetPreamble()); }
            catch { return null; }
        }
        app.Application.SharedParametersFilename = path;
        return app.Application.OpenSharedParameterFile();
    }
    catch (Exception ex)
    {
        GrdLog.Log("OpenOrCreateSharedFile EXCEPTION: " + ex);
        return null;
    }
}

/// <summary>
/// True, если общий параметр со статусом «Добавить» уже привязан к категории
/// семейства в проекте (BindingMap) тем же способом (тип/экземпляр): элементы
/// семейства параметр уже имеют, вшивать его в семейство не нужно.
/// </summary>
private static bool AlreadyCoveredByProjectBinding(Document doc, Family family, FamilyParamOp op)
{
    try
    {
        if (op == null || op.Remove) return false;
        if (op.Source != ParamSourceKind.Shared) return false;
        if (!string.Equals(op.Status, "Добавить", StringComparison.OrdinalIgnoreCase)) return false;
        var binding = ProjectBindingFor(doc, family, op);
        if (binding == null) return false;
        return (binding is InstanceBinding) == op.IsInstance;
    }
    catch (Exception ex)
    {
        GrdLog.Log("FamilyParamsHandler.AlreadyCoveredByProjectBinding EXCEPTION: " + ex);
        return false;
    }
}

/// <summary>Семейство, подготовленное к перезагрузке (SaveAs во временный файл
/// уже выполнен). LoadBatch загружает такие семейства пакетом.</summary>
private sealed class ReadyFamily
{
    public string FamilyName;
    public int SameName;
    public string TempPath;
    public string LoadedFamilyName = string.Empty;
    public string Error;                       // ошибка этого семейства при пакетной загрузке
    public readonly List<string> Added = new List<string>();
    public readonly List<string> Removed = new List<string>();
    public readonly List<string> Skipped = new List<string>();
    public readonly List<string> Already = new List<string>();
    public readonly List<string> Rebound = new List<string>();
    public readonly HashSet<string> ReboundNamesSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public readonly List<BindingSnapshot> BindSnapshots = new List<BindingSnapshot>();
    public readonly List<ParamValueSnapshot> ValueSnapshots = new List<ParamValueSnapshot>();
}

/// <summary>Строка итогового отчёта по перезагруженному семейству.</summary>
private static string BuildFamilyLine(ReadyFamily f)
{
    var parts = new List<string>();
    if (f.Added.Count > 0) parts.Add("добавлено: " + string.Join(", ", f.Added));
    if (f.Already.Count > 0) parts.Add("уже есть: " + string.Join(", ", f.Already));
    if (f.Removed.Count > 0) parts.Add("удалено: " + string.Join(", ", f.Removed));
    if (f.Skipped.Count > 0) parts.Add("пропущено: " + string.Join(", ", f.Skipped));
    if (f.Rebound.Count > 0) parts.Add("смена привязки (проект): " + string.Join(", ", f.Rebound));
    if (f.SameName > 1)
        parts.Add("ПРЕДУПРЕЖДЕНИЕ: в проекте " + f.SameName +
                  " семейства с именем «" + f.FamilyName + "» (дубликат) — перезагружено одно." +
                  " Удалите дубль через «Управление семействами/Реторт», иначе результат может потеряться.");
    return "«" + f.FamilyName + "»: " + (parts.Count > 0 ? string.Join("; ", parts) : "нет изменений");
}

private static void DeleteTempFile(string tempPath)
{
    if (string.IsNullOrEmpty(tempPath)) return;
    try { if (System.IO.File.Exists(tempPath)) System.IO.File.Delete(tempPath); } catch { }
}

private static void DeleteTempFiles(List<ReadyFamily> pending)
{
    if (pending == null) return;
    foreach (var f in pending) DeleteTempFile(f.TempPath);
}

        /// <summary>
        /// Операция «снять формулу»: у заданного параметра убирается формула во всех
        /// переданных семействах (у всех типов), после чего каждое семейство
        /// перезагружается в проект. Используется перед сменой привязки
        /// «экземпляр ↔ тип»: параметр, связанный с формулой, Revit не даёт
        /// переключить/отредактировать.
        /// </summary>
        private static FamilyParamsResult ApplyClearFormula(UIApplication app, List<string> names, string paramName)
        {
            var result = new FamilyParamsResult();
            var uiDoc = app?.ActiveUIDocument;
            if (uiDoc?.Document == null)
            {
                result.Errors.Add("Нет активного документа Revit.");
                result.TotalFamilies = names == null ? 0 : names.Count;
                return result;
            }
            var doc = uiDoc.Document;
            result.TotalFamilies = names == null ? 0 : names.Count;
            if (result.TotalFamilies == 0)
            {
                result.Errors.Add("Не выбрано ни одного семейства.");
                return result;
            }
            if (string.IsNullOrWhiteSpace(paramName))
            {
                result.Errors.Add("Не задано имя параметра.");
                return result;
            }

            foreach (var familyName in names)
            {
                var line = ClearFormulaOne(doc, familyName, paramName);
                if (line.StartsWith("!")) result.Errors.Add(line);
                else { result.AppliedFamilies++; result.Summary.Add(line); }
            }
            return result;
        }

        /// <summary>Снимает формулу у параметра в одном семействе (все типы) и
        /// перезагружает семейство в проект. Возвращает строку отчёта; `!` в начале —
        /// ошибка.</summary>
        private static string ClearFormulaOne(Document doc, string familyName, string paramName)
        {
            Family family = null;
            foreach (Element e in new FilteredElementCollector(doc).OfClass(typeof(Family)))
            {
                var f = e as Family;
                if (f == null || string.IsNullOrEmpty(f.Name)) continue;
                if (string.Equals(f.Name, familyName, StringComparison.OrdinalIgnoreCase))
                {
                    family = f;
                    break;
                }
            }
            if (family == null) return "! Семейство «" + familyName + "» не найдено в документе.";
            if (family.IsInPlace)
                return "! «" + familyName + "»: встроенное (in-place) семейство — файла семейства нет, формулу снять нельзя.";
            if (!family.IsEditable)
                return "! «" + familyName + "»: семейство недоступно для редактирования.";

            Document fdoc = null;
            try { fdoc = doc.EditFamily(family); }
            catch (Exception ex)
            {
                return "! «" + familyName + "»: не удалось открыть семейство: " + ex.Message;
            }

            int cleared = 0;
            try
            {
                using (var ft = new Transaction(fdoc, "JTOOLS: снять формулу «" + paramName + "» («" + familyName + "»)"))
                {
                    ft.Start();
                    try
                    {
                        var fm = fdoc.FamilyManager;
                        if (fm != null && fm.Types != null)
                        {
                            foreach (FamilyType type in fm.Types)
                            {
                                fm.CurrentType = type;
                                foreach (FamilyParameter fp in fm.GetParameters())
                                {
                                    if (fp?.Definition == null) continue;
                                    if (!string.Equals(fp.Definition.Name, paramName, StringComparison.OrdinalIgnoreCase)) continue;
                                    bool hasFormula = false;
                                    try { hasFormula = fp.IsDeterminedByFormula; } catch { }
                                    if (!hasFormula) continue;
                                    fm.SetFormula(fp, null);
                                    cleared++;
                                }
                            }
                        }
                    }
                    catch
                    {
                        try { ft.RollBack(); } catch { }
                        throw;
                    }
                    ft.Commit();
                }

                if (cleared == 0)
                    return "«" + familyName + "»: параметр «" + paramName + "» не определён формулой (снимать нечего)";

                string tempPath = null;
                bool loaded = false;
                using (var t = new Transaction(doc, "JTOOLS: перезагрузка семейства «" + familyName + "»"))
                {
                    t.Start();
                    try
                    {
                        tempPath = System.IO.Path.Combine(
                            System.IO.Path.GetTempPath(),
                            SafeFamilyFileName(familyName));
                        if (System.IO.File.Exists(tempPath)) System.IO.File.Delete(tempPath);
                        fdoc.SaveAs(tempPath, new SaveAsOptions());
                        loaded = doc.LoadFamily(tempPath, new FamilyLoadOptionsImpl(), out var loadedFamily);
                        t.Commit();
                    }
                    catch
                    {
                        try { t.RollBack(); } catch { }
                        throw;
                    }
                    finally
                    {
                        if (!string.IsNullOrEmpty(tempPath))
                        {
                            try { if (System.IO.File.Exists(tempPath)) System.IO.File.Delete(tempPath); } catch { }
                        }
                    }
                }
                if (!loaded)
                    return "! «" + familyName + "»: LoadFamily вернул false — формула не снята.";

                if (ParamStillReadOnly(doc, familyName, paramName))
                    return "! «" + familyName + "»: параметр «" + paramName + "» остался только для чтения — формула не снята.";

                GrdLog.Log("FamilyParamsHandler: снята формула «" + paramName + "» в «" + familyName + "» (типы=" + cleared + ")");
                return "«" + familyName + "»: формула снята («" + paramName + "», все типы)";
            }
            catch (Exception ex)
            {
                GrdLog.Log("FamilyParamsHandler.ClearFormulaOne «" + familyName + "» EXCEPTION: " + ex);
                return "! «" + familyName + "»: " + ex.Message;
            }
            finally
            {
                try { fdoc.Dispose(); } catch { }
            }
        }

        /// <summary>Проверка фактического состояния выбранных семейств (только чтение):
        /// открывает документ каждого семейства и сверяет, есть ли в нём каждый параметр
        /// из <paramref name="ops"/> — и в диспетчере параметров, и на типе в проекте.
        /// Отчёт по каждому семейству — в Summary, ошибки — в Errors.</summary>
        private static FamilyParamsResult ApplyVerify(UIApplication app, List<string> names, List<FamilyParamOp> ops)
        {
            var result = new FamilyParamsResult();
            var uiDoc = app?.ActiveUIDocument;
            if (uiDoc?.Document == null)
            {
                result.Errors.Add("Нет активного документа Revit.");
                result.TotalFamilies = names == null ? 0 : names.Count;
                return result;
            }
            var doc = uiDoc.Document;
            result.TotalFamilies = names == null ? 0 : names.Count;
            if (names == null || names.Count == 0)
            {
                result.Errors.Add("Не выбрано ни одного семейства.");
                return result;
            }
            var checkNames = (ops ?? new List<FamilyParamOp>())
                .Where(o => o != null && !string.IsNullOrEmpty(o.Name))
                .Select(o => o.Name.Trim())
                .Where(n => n.Length > 0)
                .ToList();
            GrdLog.Log("FamilyParamsHandler.ApplyVerify: семейств=" + names.Count +
                       ", проверяемых параметров=" + checkNames.Count + ": " + string.Join("; ", checkNames));

            foreach (var familyName in names)
            {
                try
                {
                    var line = VerifyOne(doc, familyName, checkNames);
                    if (line.StartsWith("!")) result.Errors.Add(line);
                    else { result.AppliedFamilies++; result.Summary.Add(line); }
                }
                catch (Exception ex)
                {
                    GrdLog.Log("FamilyParamsHandler.ApplyVerify «" + familyName + "» EXCEPTION: " + ex);
                    result.Errors.Add("«" + familyName + "»: " + ex.Message);
                }
            }
            return result;
        }

        /// <summary>Отчёт по одному семейству: количество параметров и для каждого
        /// проверяемого имени — есть/нет (в диспетчере параметров и на типе в проекте).</summary>
        private static string VerifyOne(Document doc, string familyName, IList<string> paramNames)
        {
            var family = FindFamilyByName(doc, familyName);
            if (family == null) return "! «" + familyName + "» не найдено в документе.";

            Document fdoc = null;
            try { fdoc = doc.EditFamily(family); }
            catch (Exception ex)
            {
                return "! «" + familyName + "»: не удалось открыть семейство: " + ex.Message;
            }

            var inFm = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int fmCount = 0;
            try
            {
                var fm = fdoc.FamilyManager;
                if (fm != null)
                {
                    foreach (FamilyParameter fp in fm.GetParameters())
                    {
                        if (fp?.Definition?.Name == null) continue;
                        fmCount++;
                        inFm.Add(fp.Definition.Name);
                    }
                }
            }
            catch (Exception ex)
            {
                GrdLog.Log("FamilyParamsHandler.VerifyOne «" + familyName + "» FM EXCEPTION: " + ex);
                return "! «" + familyName + "»: " + ex.Message;
            }
            finally
            {
                try { fdoc.Dispose(); } catch { }
            }

            // Параметры, реально доступные на типе семейства в проекте (после LoadFamily).
            var onType = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var ids = family.GetFamilySymbolIds();
                if (ids != null)
                {
                    foreach (var sid in ids)
                    {
                        var sym = doc.GetElement(sid) as FamilySymbol;
                        if (sym?.Parameters == null) continue;
                        foreach (Parameter p in sym.Parameters)
                        {
                            if (p?.Definition?.Name == null) continue;
                            onType.Add(p.Definition.Name);
                        }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                GrdLog.Log("FamilyParamsHandler.VerifyOne «" + familyName + "» symbols EXCEPTION: " + ex);
            }

            var sb = new System.Text.StringBuilder();
            sb.Append("«").Append(familyName).Append("»: параметров в семействе ").Append(fmCount);
            for (int i = 0; i < paramNames.Count; i++)
            {
                var p = paramNames[i];
                bool fmHas = inFm.Contains(p);
                bool typeHas = onType.Contains(p);
                sb.Append("; «").Append(p).Append("»: ").Append(fmHas ? "есть" : "НЕТ");
                if (fmHas != typeHas)
                    sb.Append(" (в проекте: ").Append(typeHas ? "есть" : "нет").Append(")");
            }
            GrdLog.Log("FamilyParamsHandler.VerifyOne: «" + familyName + "» параметров=" + fmCount);
            return sb.ToString();
        }

        /// <summary>Снимок значения параметра, который переносится из проекта в семейство:
        /// экземпляры при перезагрузке семейства не меняются (ищем по ElementId),
        /// типы — пересоздаются (ищем по имени типа).</summary>
        private sealed class ParamValueSnapshot
        {
            public string ParamName = string.Empty;
            public string SymbolName;          // для типа: имя типа
            public ElementId InstanceId;       // для экземпляра: элемент документа
            public bool HasValue;
            public string ValueString = string.Empty;
            public bool IsStringStorage;
        }

        /// <summary>Привязка общего параметра в ДОКУМЕНТЕ к категории семейства
        /// (InstanceBinding/TypeBinding), либо null, если параметр в проект не привязан
        /// (тогда и сохранять значения нечего).</summary>
        private static Binding ProjectBindingFor(Document doc, Family family, FamilyParamOp op)
        {
            try
            {
                var famCat = family.Category;
                if (famCat == null) return null;
                foreach (object entry in doc.ParameterBindings)
                {
                    Definition def = null;
                    Binding binding = null;
                    if (entry is KeyValuePair<Definition, Binding> kp) { def = kp.Key; binding = kp.Value; }
                    else if (entry is System.Collections.DictionaryEntry de) { def = de.Key as Definition; binding = de.Value as Binding; }
                    var eb = binding as ElementBinding;
                    if (def == null || eb == null) continue;
                    if (!string.Equals(def.Name, op.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    var cats = eb.Categories;
                    if (cats == null) continue;
                    foreach (Category c in cats)
                    {
                        if (c == null) continue;
                        if (c.Id == famCat.Id || string.Equals(c.Name, famCat.Name, StringComparison.OrdinalIgnoreCase))
                            return binding;
                    }
                }
                return null;
            }
            catch (Exception ex)
            {
                GrdLog.Log("FamilyParamsHandler.ProjectBindingFor «" + op.Name + "» EXCEPTION: " + ex);
                return null;
            }
        }

        /// <summary>Смена «экземпляр ↔ тип» для параметра, привязанного к категории
        /// в ПРОЕКТЕ, но не встроенного в семейство (в FamilyManager его нет):
        /// перепривязка в BindingMap новым InstanceBinding/TypeBinding.
        /// ReInsert без параметра группы сохраняет текущую группировку параметра.</summary>
        private static bool TryProjectRebind(Document doc, Family family, FamilyParamOp op)
        {
            try
            {
                var famCat = family?.Category;
                if (famCat == null) return false;
                foreach (object entry in doc.ParameterBindings)
                {
                    Definition def = null;
                    Binding binding = null;
                    if (entry is KeyValuePair<Definition, Binding> kp) { def = kp.Key; binding = kp.Value; }
                    else if (entry is System.Collections.DictionaryEntry de) { def = de.Key as Definition; binding = de.Value as Binding; }
                    else continue;
                    var eb = binding as ElementBinding;
                    if (def == null || eb == null) continue;
                    if (!string.Equals(def.Name, op.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    var cats = eb.Categories;
                    if (cats == null) continue;
                    bool match = false;
                    foreach (Category c in cats)
                    {
                        if (c == null) continue;
                        if (c.Id == famCat.Id || string.Equals(c.Name, famCat.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            match = true;
                            break;
                        }
                    }
                    if (!match) continue;
                    Binding nb = op.IsInstance ? (Binding)new InstanceBinding(cats) : new TypeBinding(cats);
                    return doc.ParameterBindings.ReInsert(def, nb);
                }
                return false;
            }
            catch (Exception ex)
            {
                GrdLog.Log("FamilyParamsHandler.TryProjectRebind «" + op.Name + "» EXCEPTION: " + ex);
                return false;
            }
        }

        /// <summary>Захватывает текущие значения параметра на всех экземплярах (при
        /// instance-привязке) либо на всех типах (при type-привязке) семейства в документе.</summary>
        private static List<ParamValueSnapshot> CaptureValues(Document doc, Family family, string paramName, Binding binding)
        {
            var list = new List<ParamValueSnapshot>();
            try
            {
                if (binding is InstanceBinding)
                {
                    foreach (Element e in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)))
                    {
                        var fi = e as FamilyInstance;
                        if (fi?.Symbol?.Family == null) continue;
                        if (fi.Symbol.Family.Id != family.Id) continue;
                        var p = fi.LookupParameter(paramName);
                        var s = CaptureOne(p, fi.Id, null);
                        if (s != null) list.Add(s);
                    }
                }
                else
                {
                    var ids = family.GetFamilySymbolIds();
                    if (ids == null) return list;
                    foreach (var sid in ids)
                    {
                        var sym = doc.GetElement(sid) as FamilySymbol;
                        if (sym == null) continue;
                        var p = sym.LookupParameter(paramName);
                        var s = CaptureOne(p, ElementId.InvalidElementId, sym.Name);
                        if (s != null) list.Add(s);
                    }
                }
            }
            catch (Exception ex)
            {
                GrdLog.Log("FamilyParamsHandler.CaptureValues «" + paramName + "» EXCEPTION: " + ex);
            }
            GrdLog.Log("FamilyParamsHandler.CaptureValues: «" + paramName + "» экземпляров/типов=" + list.Count);
            return list;
        }

        private static ParamValueSnapshot CaptureOne(Parameter p, ElementId instanceId, string symbolName)
        {
            if (p == null) return null;
            try
            {
                var snap = new ParamValueSnapshot
                {
                    ParamName = p.Definition?.Name ?? string.Empty,
                    SymbolName = symbolName,
                    InstanceId = instanceId,
                    IsStringStorage = p.StorageType == StorageType.String
                };
                try { snap.HasValue = p.HasValue; } catch { }
                if (snap.HasValue)
                    snap.ValueString = snap.IsStringStorage ? p.AsString() : p.AsValueString();
                return snap;
            }
            catch (Exception ex)
            {
                GrdLog.Log("FamilyParamsHandler.CaptureOne EXCEPTION: " + ex);
                return null;
            }
        }

        /// <summary>Возвращает значения перенесённых параметров после перезагрузки семейства:
        /// текст — Set(string), иначе SetValueString (значение с единицами, как было).</summary>
        private static void RestoreValues(Document doc, string familyName, List<ParamValueSnapshot> snaps)
        {
            if (snaps == null || snaps.Count == 0) return;
            int restored = 0, failed = 0;
            Family family = null;
            try
            {
                foreach (var s in snaps)
                {
                    try
                    {
                        Parameter p = null;
                        if (s.InstanceId == null || s.InstanceId == ElementId.InvalidElementId)
                        {
                            if (family == null) family = FindFamilyByName(doc, familyName);
                            if (family == null) { failed++; continue; }
                            var ids = family.GetFamilySymbolIds();
                            if (ids == null) { failed++; continue; }
                            foreach (var sid in ids)
                            {
                                var sym = doc.GetElement(sid) as FamilySymbol;
                                if (sym == null || !string.Equals(sym.Name, s.SymbolName, StringComparison.OrdinalIgnoreCase)) continue;
                                p = sym.LookupParameter(s.ParamName);
                                break;
                            }
                        }
                        else
                        {
                            var el = doc.GetElement(s.InstanceId);
                            p = el?.LookupParameter(s.ParamName);
                        }
                        if (p == null) { failed++; continue; }
                        if (!s.HasValue) continue;
                        if (s.IsStringStorage) p.Set(s.ValueString);
                        else p.SetValueString(s.ValueString);
                        restored++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        GrdLog.Log("FamilyParamsHandler.RestoreValues «" + s.ParamName + "» EXCEPTION: " + ex);
                    }
                }
            }
            finally
            {
                GrdLog.Log("FamilyParamsHandler.RestoreValues: восстановлено=" + restored + ", не удалось=" + failed);
            }
        }

        private static Family FindFamilyByName(Document doc, string familyName)
        {
            foreach (Element e in new FilteredElementCollector(doc).OfClass(typeof(Family)))
            {
                var f = e as Family;
                if (f == null || string.IsNullOrEmpty(f.Name)) continue;
                if (string.Equals(f.Name, familyName, StringComparison.OrdinalIgnoreCase)) return f;
            }
            return null;
        }

        /// <summary>Проверка после перезагрузки: параметр не должен остаться
        /// только для чтения (иначе формула, по сути, не снята). True только при
        /// явном подтверждении IsReadOnly; если параметр не найден — не блокируем.</summary>
        private static bool ParamStillReadOnly(Document doc, string familyName, string paramName)
        {
            try
            {
                foreach (Element e in new FilteredElementCollector(doc).OfClass(typeof(Family)))
                {
                    var f = e as Family;
                    if (f == null || string.IsNullOrEmpty(f.Name)) continue;
                    if (!string.Equals(f.Name, familyName, StringComparison.OrdinalIgnoreCase)) continue;
                    var ids = f.GetFamilySymbolIds();
                    if (ids == null) continue;
                    foreach (var sid in ids)
                    {
                        var sym = doc.GetElement(sid) as FamilySymbol;
                        if (sym == null) continue;
                        var p = sym.LookupParameter(paramName);
                        if (p != null) return p.IsReadOnly;
                    }
                    break;
                }
            }
            catch (Exception ex)
            {
                GrdLog.Log("FamilyParamsHandler.ParamStillReadOnly EXCEPTION: " + ex);
            }
            return false;
        }

        /// <summary>
        /// Диагностика после LoadFamily: сколько семейств в документе, сколько из них
        /// с тем же именем, какие новые имена появились (если LoadFamily создал
        /// семейство под другим именем), сколько пользовательских параметров видно
        /// и какие из добавленных присутствуют.
        /// </summary>
        private static void VerifyAfterLoad(Document doc, string familyName, List<string> beforeNames, List<string> added)
        {
            try
            {
                var afterNames = FamilyNames(doc);
                var appended = afterNames.Except(beforeNames, StringComparer.OrdinalIgnoreCase).ToList();
                var removed = beforeNames.Except(afterNames, StringComparer.OrdinalIgnoreCase).ToList();

                int famCount = afterNames.Count;
                var matches = new List<string>();
                var userParamsTotal = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Element e in new FilteredElementCollector(doc).OfClass(typeof(Family)))
                {
                    if (string.IsNullOrEmpty(e.Name)) continue;
                    if (!string.Equals(e.Name, familyName, StringComparison.OrdinalIgnoreCase)) continue;
                    var fam = e as Family;
                    var detail = "Id=" + e.Id.Value;
                    if (fam != null)
                    {
                        // Сколько экземпляров ссылается на это семейство.
                        int instCount = 0;
                        try
                        {
                            foreach (FamilyInstance fi in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)))
                            {
                                if (fi.Symbol?.Family != null && fi.Symbol.Family.Id == fam.Id) instCount++;
                            }
                        }
                        catch { }
                        detail += ", экземпляров=" + instCount;
                        try
                        {
                            var syms = fam.GetFamilySymbolIds();
                            if (syms == null || syms.Count == 0)
                            {
                                detail += ", символов=0";
                            }
                            else
                            {
                                // Какие из добавлённых параметров реально видны на символах
                                // хотя бы этого семейства.
                                var presentNow = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                                foreach (var sid in syms)
                                {
                                    var sym = doc.GetElement(sid) as FamilySymbol;
                                    if (sym?.Parameters == null) continue;
                                    foreach (Parameter p in sym.Parameters)
                                    {
                                        if (p?.Definition == null) continue;
                                        if (p.Definition is InternalDefinition pp && pp.BuiltInParameter != BuiltInParameter.INVALID) continue;
                                        var pn = p.Definition.Name;
                                        if (string.IsNullOrEmpty(pn)) continue;
                                        userParamsTotal.Add(pn);
                                        if (added.Any(a => string.Equals(a, pn, StringComparison.OrdinalIgnoreCase)))
                                            presentNow.Add(pn);
                                    }
                                }
                                detail += ", символов=" + syms.Count +
                                    (presentNow.Count > 0
                                        ? ", В СЕМЕЙСТВЕ ЕСТЬ: " + string.Join(", ", presentNow)
                                        : ", добавленных нет");
                            }
                        }
                        catch (Exception ex)
                        {
                            detail += ", параметры EXCEPTION: " + ex.Message;
                        }
                    }
                    matches.Add(detail);
                }

                var present = added.Where(n => userParamsTotal.Any(u =>
                    string.Equals(u, n, StringComparison.OrdinalIgnoreCase))).ToList();
                GrdLog.Log("FamilyParamsHandler.Verify: семейств=" + famCount +
                           " (было " + beforeNames.Count + "), " +
                           (appended.Count > 0
                               ? "ПОЯВИЛИСЬ семейства: " + string.Join("; ", appended) + "; "
                               : "новых семейств нет; ") +
                           (removed.Count > 0 ? "исчезли: " + string.Join("; ", removed) + "; " : string.Empty) +
                           "семейства с именем «" + familyName + "»=" + matches.Count +
                           (matches.Count > 1 ? " (ВНИМАНИЕ: дубль!)" : string.Empty) +
                           "; подробно: " + string.Join(" | ", matches) +
                           ", пользовательских параметров=" + userParamsTotal.Count +
                           ", из добавленных присутствует: " + (present.Count > 0 ? string.Join(", ", present) : "<нет>"));
            }
            catch (Exception ex)
            {
                GrdLog.Log("FamilyParamsHandler.Verify EXCEPTION: " + ex);
            }
        }

        /// <summary>Имя временного RFA-файла = имя семейства (+.rfa), с заменой
        /// недопустимых в имени файла символов.</summary>
        private static string SafeFamilyFileName(string familyName)
        {
            var s = string.IsNullOrEmpty(familyName) ? "Family" : familyName;
            foreach (var c in System.IO.Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');
            return s + ".rfa";
        }

        /// <summary>Имя временного файла для перезагрузки: берём имя файла, из которого
        /// семейство загружено в проект (PathName документа семейства). При совпадении
        /// имени Revit расценивает файл как тот же и заменяет семейство на месте; при
        /// расхождении (например, лишний пробел перед .rfa) может создать дубль семейства.
        /// Если путь неизвестен — используем имя семейства.</summary>
        private static string FamilyReloadFileName(Document fdoc, string familyName)
        {
            try
            {
                var path = fdoc?.PathName;
                if (!string.IsNullOrEmpty(path))
                {
                    var baseName = System.IO.Path.GetFileName(path);
                    if (!string.IsNullOrEmpty(baseName)) return baseName;
                }
            }
            catch (Exception ex)
            {
                GrdLog.Log("FamilyParamsHandler.FamilyReloadFileName EXCEPTION: " + ex);
            }
            return SafeFamilyFileName(familyName);
        }

        /// <summary>Отсортированный список имён всех семейств документа.</summary>
        private static List<string> FamilyNames(Document doc)
        {
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Element e in new FilteredElementCollector(doc).OfClass(typeof(Family)))
            {
                if (!string.IsNullOrEmpty(e.Name)) names.Add(e.Name);
            }
            return names.ToList();
        }

        private static void ApplyOp(Document fdoc, UIApplication app, FamilyParamOp op,
            List<string> added, List<string> removed, List<string> skipped, List<string> already)
        {
            var fm = fdoc.FamilyManager;
            if (op.Remove)
            {
                var existing = FindParam(fm, op);
                if (existing == null)
                {
                    skipped.Add(op.Name + " (не найден)");
                    return;
                }
                fm.RemoveParameter(existing);
                removed.Add(op.Name);
                return;
            }

            if (string.Equals(op.Status, "Изменить привязку", StringComparison.OrdinalIgnoreCase))
            {
                var existing = FindParam(fm, op);
                if (existing == null)
                {
                    skipped.Add(op.Name + " (не найден)");
                    return;
                }
                if (existing.IsInstance == op.IsInstance)
                {
                    skipped.Add(op.Name + " (уже " + (op.IsInstance ? "экземпляр" : "тип") + ")");
                    return;
                }
                try
                {
                    if (op.IsInstance) fm.MakeInstance(existing);
                    else fm.MakeType(existing);
                    removed.Add(op.Name + " (" + (op.IsInstance ? "тип→экземпляр" : "экземпляр→тип") + ")");
                }
                catch (Exception ex)
                {
                    skipped.Add(op.Name + " (ошибка: " + ex.Message + ")");
                }
                return;
            }

            // Добавление: если параметр в этом семействе УЖЕ есть (с тем же именем),
            // не пытаемся добавить повторно (Revit это запрещает) — просто пропускаем
            // с пометкой «уже есть». Так параметр добавляется только тем семействам,
            // в которых его нет, а в остальных остаётся как есть.
            if (FindParam(fm, op) != null)
            {
                already.Add(op.Name);
                return;
            }

            FamilyParameter fp = op.Source == ParamSourceKind.Shared
                ? AddShared(fdoc, app, op)
                : AddFamily(fdoc, op);
            if (fp != null) added.Add(fp.Definition?.Name ?? op.Name);
        }

        private static FamilyParameter AddFamily(Document fdoc, FamilyParamOp op)
        {
            var fm = fdoc.FamilyManager;
            var gt = GroupFor(op.Group);
            switch (op.StorageType)
            {
                case "Целое":
                    return fm.AddParameter(op.Name, gt, SpecTypeId.Int.Integer, op.IsInstance);
                case "Число":
                    return fm.AddParameter(op.Name, gt, SpecTypeId.Number, op.IsInstance);
                case "Длина":
                    return fm.AddParameter(op.Name, gt, SpecTypeId.Length, op.IsInstance);
                case "Площадь":
                    return fm.AddParameter(op.Name, gt, SpecTypeId.Area, op.IsInstance);
                case "Объём":
                    return fm.AddParameter(op.Name, gt, SpecTypeId.Volume, op.IsInstance);
                default:
                    return fm.AddParameter(op.Name, gt, SpecTypeId.String.Text, op.IsInstance);
            }
        }

        private static FamilyParameter AddShared(Document fdoc, UIApplication app, FamilyParamOp op)
        {
            var spf = app.Application.OpenSharedParameterFile();
            if (spf == null)
                throw new InvalidOperationException("Файл общих параметров не открыт: загрузите файл общих параметров (.txt) в окне");

            var def = FindSharedDef(spf, op);
            GrdLog.Log("FamilyParamsHandler.AddShared: файл=" + spf.Filename +
                       ", guid=" + op.SharedGuid + ", имя=" + op.Name +
                       ", def найден=" + (def != null));
            if (def == null)
            {
                var groupName = string.IsNullOrEmpty(op.Group) ? "Параметры" : op.Group;
                var grp = spf.Groups.Create(groupName);
                var options = new ExternalDefinitionCreationOptions(op.Name, SharedStorage(op.StorageType))
                {
                    GUID = string.IsNullOrEmpty(op.SharedGuid)
                        ? System.Guid.NewGuid()
                        : System.Guid.Parse(op.SharedGuid)
                };
                def = grp.Definitions.Create(options) as ExternalDefinition;
                GrdLog.Log("FamilyParamsHandler.AddShared: создано новое определение, GUID=" +
                           (def != null ? def.GUID.ToString() : "<null>"));
                if (def == null)
                    throw new InvalidOperationException("Не удалось создать определение общего параметра «" + op.Name + "»");
            }

            var fp = fdoc.FamilyManager.AddParameter(def, GroupFor(op.Group), op.IsInstance);
            GrdLog.Log("FamilyParamsHandler.AddShared: AddParameter OK, имя=" +
                       (fp?.Definition?.Name ?? "<null>") + ", экземпляр=" + op.IsInstance);
            return fp;
        }

        private static ExternalDefinition FindSharedDef(DefinitionFile file, FamilyParamOp op)
        {
            foreach (DefinitionGroup g in file.Groups)
            {
                if (g == null) continue;
                foreach (Definition d in g.Definitions)
                {
                    var ext = d as ExternalDefinition;
                    if (ext == null) continue;
                    if (System.Guid.TryParse(op.SharedGuid, out var guid) &&
                        guid != System.Guid.Empty && ext.GUID == guid) return ext;
                    if (string.Equals(ext.Name, op.Name, StringComparison.OrdinalIgnoreCase)) return ext;
                }
            }
            return null;
        }

        private static FamilyParameter FindParam(FamilyManager fm, FamilyParamOp op)
        {
            foreach (FamilyParameter fp in fm.GetParameters())
            {
                if (fp?.Definition == null) continue;
                if (string.Equals(fp.Definition.Name, op.Name, StringComparison.OrdinalIgnoreCase)) return fp;
            }
            return null;
        }

        private static ForgeTypeId SharedStorage(string storageType)
        {
            switch (storageType)
            {
                case "Целое": return SpecTypeId.Int.Integer;
                case "Число": return SpecTypeId.Number;
                case "Длина": return SpecTypeId.Length;
                case "Площадь": return SpecTypeId.Area;
                case "Объём": return SpecTypeId.Volume;
                default: return SpecTypeId.String.Text;
            }
        }

        /// <summary>Группа параметра в свойствах семейства (ForgeTypeId).</summary>
        private static ForgeTypeId GroupFor(string group)
        {
            if (string.IsNullOrEmpty(group)) return GroupTypeId.General;
            switch (group.Trim().ToLowerInvariant())
            {
                case "данные": return GroupTypeId.Data;
                case "текст": return GroupTypeId.Text;
                case "конструкция": return GroupTypeId.Construction;
                case "механика": return GroupTypeId.Mechanical;
                case "электрика": return GroupTypeId.Electrical;
                default: return GroupTypeId.General;
            }
        }

        private sealed class FamilyLoadOptionsImpl : IFamilyLoadOptions
        {
            // overwriteParameterValues = false: при перезагрузке семейства значения
            // параметров из проекта сохраняются (в семейство перезаписываются только
            // новые параметры). Иначе Revit затирает заполненные в проекте значения
            // значениями из RFA (пустыми), в т.ч. при смене привязки «экземпляр ↔ тип».
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                overwriteParameterValues = false;
                return true;
            }

            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            {
                source = FamilySource.Family;
                overwriteParameterValues = false;
                return true;
            }
        }

        /// <summary>Снимок значений параметра, у которого меняется привязка
        /// «экземпляр ↔ тип», до перезагрузки семейства. После LoadFamily значения
        /// восстанавливаются (см. RestoreBinding), иначе Revit их обнуляет.</summary>
        private sealed class BindingSnapshot
        {
            public string ParamName = string.Empty;
            public bool WasInstance;                                  // привязка до изменения
            public readonly Dictionary<long, string> InstanceValues = new Dictionary<long, string>(); // elementId -> значение
            public readonly Dictionary<long, long> ElementToSymbol = new Dictionary<long, long>();    // elementId -> symbolId
            public readonly Dictionary<long, string> TypeValues = new Dictionary<long, string>();     // symbolId -> значение
        }

        private static BindingSnapshot CaptureBinding(Document doc, string familyName, string paramName, bool wasInstance)
        {
            var snap = new BindingSnapshot { ParamName = paramName, WasInstance = wasInstance };
            if (wasInstance)
            {
                foreach (FamilyInstance fi in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)))
                {
                    try
                    {
                        if (fi.Symbol?.Family == null ||
                            !string.Equals(fi.Symbol.Family.Name, familyName, StringComparison.OrdinalIgnoreCase)) continue;
                        var p = fi.LookupParameter(paramName);
                        if (p == null) continue;
                        snap.InstanceValues[fi.Id.Value] = ReadParamValue(p);
                        snap.ElementToSymbol[fi.Id.Value] = fi.Symbol.Id.Value;
                    }
                    catch { }
                }
            }
            else
            {
                foreach (FamilySymbol sym in new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)))
                {
                    try
                    {
                        if (sym?.Family == null ||
                            !string.Equals(sym.Family.Name, familyName, StringComparison.OrdinalIgnoreCase)) continue;
                        var p = sym.LookupParameter(paramName);
                        if (p == null) continue;
                        snap.TypeValues[sym.Id.Value] = ReadParamValue(p);
                    }
                    catch { }
                }
            }
            GrdLog.Log("FamilyParamsHandler.Capture: «" + paramName + "» привязка=" +
                       (snap.WasInstance ? "экз." : "тип") +
                       ", экземпляров=" + snap.InstanceValues.Count +
                       ", типов=" + snap.TypeValues.Count);
            return snap;
        }

        /// <summary>Восстанавливает значения параметра после смены привязки.
        /// <para>экземпляр → тип: у типа одно значение на тип — берём значение,
        /// общее для всех экземпляров типа, либо самое частое / первое непустое.</para>
        /// <para>тип → экземпляр: раздаём каждому экземпляру сохранённое значение его типа.</para>
        /// </summary>
        private static void RestoreBinding(Document doc, string familyName, BindingSnapshot snap)
        {
            int set = 0;
            if (snap.WasInstance)
            {
                var perType = new Dictionary<long, Dictionary<string, int>>();
                foreach (var kv in snap.ElementToSymbol)
                {
                    if (!snap.InstanceValues.TryGetValue(kv.Key, out var v)) continue;
                    if (!perType.TryGetValue(kv.Value, out var counts))
                    {
                        counts = new Dictionary<string, int>();
                        perType[kv.Value] = counts;
                    }
                    counts[v] = counts.TryGetValue(v, out var c) ? c + 1 : 1;
                }
                foreach (var pair in perType)
                {
                    try
                    {
                        var sym = doc.GetElement(new ElementId(pair.Key)) as FamilySymbol;
                        if (sym == null) continue;
                        var value = PickValue(pair.Value);
                        if (string.IsNullOrEmpty(value)) continue;
                        var p = sym.LookupParameter(snap.ParamName);
                        if (p != null && !p.IsReadOnly && TrySetParamValue(p, value)) set++;
                    }
                    catch { }
                }
            }
            else
            {
                foreach (FamilyInstance fi in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)))
                {
                    try
                    {
                        if (fi.Symbol?.Family == null ||
                            !string.Equals(fi.Symbol.Family.Name, familyName, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!snap.TypeValues.TryGetValue(fi.Symbol.Id.Value, out var value)) continue;
                        if (string.IsNullOrEmpty(value)) continue;
                        var p = fi.LookupParameter(snap.ParamName);
                        if (p != null && !p.IsReadOnly && TrySetParamValue(p, value)) set++;
                    }
                    catch { }
                }
            }
            GrdLog.Log("FamilyParamsHandler.Restore: «" + snap.ParamName + "» установлено=" + set);
        }

        /// <summary>Выбирает значение для типа при слиянии экземпляров в один тип:
        /// единственное значение, иначе самое частое непустое (первое по порядку при равенстве).
        /// Пустые значения игнорируются — их нечего восстанавливать.</summary>
        private static string PickValue(Dictionary<string, int> counts)
        {
            string best = string.Empty;
            int bestCount = -1;
            foreach (var kv in counts)
            {
                if (string.IsNullOrEmpty(kv.Key)) continue;
                if (kv.Value > bestCount)
                {
                    best = kv.Key;
                    bestCount = kv.Value;
                }
            }
            return best;
        }

        private static string ReadParamValue(Parameter p)
        {
            try
            {
                if (!p.HasValue) return string.Empty;
                if (p.StorageType == StorageType.String) return p.AsString() ?? string.Empty;
                if (p.StorageType == StorageType.Integer)
                {
                    try { var v = p.AsValueString(); if (!string.IsNullOrEmpty(v)) return v; } catch { }
                    return p.AsInteger().ToString(CultureInfo.InvariantCulture);
                }
                if (p.StorageType == StorageType.Double)
                {
                    try { var v = p.AsValueString(); if (!string.IsNullOrEmpty(v)) return v; } catch { }
                    return p.AsDouble().ToString("0.####", CultureInfo.InvariantCulture);
                }
            }
            catch (Exception ex)
            {
                GrdLog.Log("FamilyParamsHandler.ReadParamValue EXCEPTION: " + ex);
            }
            return string.Empty;
        }

        private static bool TrySetParamValue(Parameter p, string value)
        {
            if (p == null || value == null) return false;
            try
            {
                if (p.IsReadOnly) return false;
                var spec = p.Definition?.GetDataType();
                if (spec != null && spec == SpecTypeId.Boolean.YesNo)
                {
                    var b = NormalizeBool(value);
                    if (b.HasValue) return p.Set(b.Value ? 1 : 0);
                    return false;
                }
                if (p.Set(value)) return true;
                if (p.SetValueString(value)) return true;
                switch (p.StorageType)
                {
                    case StorageType.Integer:
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ||
                            int.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out i))
                            return p.Set(i);
                        return false;
                    case StorageType.Double:
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ||
                            double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out d))
                        {
                            try { return p.Set(spec == null ? d : UnitUtils.ConvertToInternalUnits(d, spec)); }
                            catch { return p.Set(d); }
                        }
                        return false;
                }
            }
            catch (Exception ex)
            {
                GrdLog.Log("FamilyParamsHandler.TrySetParamValue EXCEPTION: " + ex);
            }
            return false;
        }

        private static bool? NormalizeBool(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return null;
            var s = v.Trim();
            if (s.Length == 1 && (s[0] == '0' || s[0] == '1')) return s[0] == '1';
            var lower = s.ToLowerInvariant();
            if (lower == "да" || lower == "yes" || lower == "true") return true;
            if (lower == "нет" || lower == "no" || lower == "false") return false;
            return null;
        }
    }
}
