using System;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using GrdRevit.Core;

namespace GrdRevit.Revit
{
    /// <summary>
    /// Параметр-маркер плагина: общий (shared) параметр-число с привязкой
    /// InstanceBinding к категориям. На элементах, которые должны попасть в
    /// спецификацию, ставится значение 1; спецификация фильтруется штатным
    /// средством Revit (ScheduleFilter Equal). Паттерн используется окном
    /// «Параметры семейств» при создании ведомости по выбранным элементам и
    /// при создании спецификации «JTOOLS: перезагрузка — пропущено».
    /// </summary>
    public static class SharedMarkerParam
    {
        /// <summary>Имя маркер-параметра ведомости по выбранным элементам (группа JTOOLS).</summary>
        public const string ScheduleMarkerName = "JTOOLS: выбор ведомости";

        /// <summary>
        /// Гарантирует наличие общего параметра-метки и его привязки к
        /// <paramref name="catSet"/>. Возвращает внешнее определение (null при неудаче).
        /// Если параметр уже создан ранее и привязка покрывает нужные категории —
        /// переиспользуется, иначе создаётся новое определение с уникальным именем
        /// на базе <paramref name="baseName"/>. Категории, не поддерживающие
        /// привязку (AllowsBoundParameters=false), в проверку не включаются.
        /// </summary>
        public static ExternalDefinition EnsureMarkerDefinition(UIApplication app, Document doc, CategorySet catSet,
            string baseName, string groupName, string description)
        {
            try
            {
                var file = OpenOrCreateSharedFile(app);
                if (file == null) return null;

                var group = FindOrCreateGroup(file, groupName);
                if (group == null)
                {
                    GrdLog.Log("SharedMarkerParam: не удалось получить группу '" + groupName
                        + "' в файле общих параметров");
                    return null;
                }

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
                    Description = string.IsNullOrEmpty(description) ? baseName : description
                }) as ExternalDefinition;
                if (def == null) return null;

                doc.ParameterBindings.Insert(def, new InstanceBinding(catSet));
                return def;
            }
            catch (Exception ex)
            {
                GrdLog.Log("SharedMarkerParam.EnsureMarkerDefinition EXCEPTION: " + ex);
                return null;
            }
        }

        /// <summary>Возвращает группу общих параметров по имени, создавая её при отсутствии.
        /// Ищет без учёта регистра, так как Revit может вернуть имя в другом регистре,
        /// а <c>Create</c> бросает исключение при дубликате. Возвращает null при неудаче.</summary>
        private static DefinitionGroup FindOrCreateGroup(DefinitionFile file, string groupName)
        {
            if (file == null || string.IsNullOrEmpty(groupName)) return null;

            try
            {
                var group = file.Groups.get_Item(groupName);
                if (group != null) return group;
            }
            catch { }

            try
            {
                foreach (DefinitionGroup g in file.Groups)
                {
                    if (string.Equals(g.Name, groupName, StringComparison.OrdinalIgnoreCase)) return g;
                }
            }
            catch { }

            try
            {
                return file.Groups.Create(groupName);
            }
            catch (Exception ex)
            {
                GrdLog.Log("SharedMarkerParam: Groups.Create('" + groupName + "') failed: " + ex);
            }

            try
            {
                foreach (DefinitionGroup g in file.Groups)
                {
                    if (string.Equals(g.Name, groupName, StringComparison.OrdinalIgnoreCase)) return g;
                }
            }
            catch { }

            return null;
        }

        /// <summary>True, если привязка <paramref name="binding"/> покрывает все категории
        /// из <paramref name="needed"/> (на самом деле — все нужные, допускающие привязку).</summary>
        public static bool CategoriesCovered(InstanceBinding binding, CategorySet needed)
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

        /// <summary>Возвращает набор категорий из набора категорий, пропуская категории,
        /// к которым нельзя привязать общий параметр (AllowsBoundParameters=false).</summary>
        public static CategorySet FilterBindable(Autodesk.Revit.ApplicationServices.Application app, CategorySet cats)
        {
            var set = app.Create.NewCategorySet();
            if (cats == null) return set;
            try
            {
                foreach (Category c in cats)
                {
                    if (c == null) continue;
                    try
                    {
                        if (c.AllowsBoundParameters) set.Insert(c);
                    }
                    catch
                    {
                        // Не удалось проверить — пробуем добавить как есть.
                        try { set.Insert(c); } catch { }
                    }
                }
            }
            catch { }
            return set;
        }

        /// <summary>Путь к локальному файлу общих параметров плагина: используется, если
        /// файл из настроек недоступен для записи (например, сетевой шар открыт только
        /// на чтение). Создаётся один раз и переиспользуется в следующих сеансах.</summary>
        public static string LocalSharedParamsPath()
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GrdRevit");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return Path.Combine(dir, "SharedParams.txt");
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Проверяет, что в файл можно записывать. Возвращает false, если доступ
        /// запрещён (сетевой шар только на чтение, запрет прав, защищённый файл).
        /// Ошибки блокировки файла другим процессом (IOException) считаем записываемостью.</summary>
        private static bool CanWriteToFile(string path)
        {
            try
            {
                using (new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) { }
                return true;
            }
            catch (UnauthorizedAccessException) { return false; }
            catch (IOException) { return true; }
            catch { return true; }
        }

        /// <summary>Проверяет, что каталог файла допускает создание файлов.</summary>
        private static bool CanWriteToDirectory(string path)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(dir)) return false;
                string probe = Path.Combine(dir, "__grd_probe_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp");
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                try { File.Delete(probe); } catch { }
                return true;
            }
            catch (UnauthorizedAccessException) { return false; }
            catch (IOException) { return false; }
            catch { return true; }
        }

        /// <summary>Создаёт пустой файл общих параметров, если его ещё нет.</summary>
        private static string EnsureEmptySharedParamsFile(string path)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                if (!File.Exists(path))
                    File.WriteAllBytes(path, System.Text.Encoding.UTF8.GetPreamble());
                return path;
            }
            catch (Exception ex)
            {
                GrdLog.Log("SharedMarkerParam.EnsureEmptySharedParamsFile EXCEPTION: " + ex);
                return null;
            }
        }

        /// <summary>Возвращает файл общих параметров для параметра-метки: использует файл
        /// текущего сеанса (Settings.LastSharedParamsPath), при недоступности для записи —
        /// локальный файл плагина, при отсутствии — создаёт новый во временной папке.</summary>
        public static DefinitionFile OpenOrCreateSharedFile(UIApplication app)
        {
            try
            {
                string path = app.Application.SharedParametersFilename;
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    path = RevitContext.Settings.LastSharedParamsPath;

                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    bool writable = CanWriteToFile(path) && CanWriteToDirectory(path);
                    if (!writable)
                    {
                        string local = EnsureEmptySharedParamsFile(LocalSharedParamsPath());
                        if (local != null)
                        {
                            GrdLog.Log("SharedMarkerParam: файл общих параметров '" + path
                                + "' недоступен для записи, используется локальный '" + local + "'");
                            path = local;
                        }
                        else
                        {
                            GrdLog.Log("SharedMarkerParam: файл '" + path
                                + "' недоступен для записи, локальный файл создать не удалось");
                        }
                    }
                }
                else
                {
                    path = Path.Combine(Path.GetTempPath(),
                        "JTOOLS_SharedParams_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".txt");
                    if (EnsureEmptySharedParamsFile(path) == null) return null;
                }

                app.Application.SharedParametersFilename = path;
                return app.Application.OpenSharedParameterFile();
            }
            catch (Exception ex)
            {
                GrdLog.Log("SharedMarkerParam.OpenOrCreateSharedFile EXCEPTION: " + ex);
                return null;
            }
        }
    }
}