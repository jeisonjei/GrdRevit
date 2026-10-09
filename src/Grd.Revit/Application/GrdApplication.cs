using System;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace GrdRevit
{
    public class GrdApplication : IExternalApplication
    {
        public const string ProductName = "JTOOLS";
        // Вкладка и панель ленты: короткие подписи; полное название — на кнопке и в окне.
        public const string TabName = "JTOOLS";
        public const string PanelName = "Данные";

        private const string ButtonDataName = "GrdRevit.Open";
        private const string FamilyParamsButtonName = "GrdRevit.FamilyParams";
        private const string Box3DButtonName = "GrdRevit.Box3D";
        private const string BoxPlanButtonName = "GrdRevit.BoxPlan";
        private const string InstanceParamsButtonName = "GrdRevit.InstanceParams";
        private const string CopyParamsButtonName = "GrdRevit.CopyParams";
        private const string ScheduleExportButtonName = "GrdRevit.ScheduleExport";
        private const string ScheduleSnapshotButtonName = "GrdRevit.ScheduleSnapshot";
        private const string PdfPrintButtonName = "GrdRevit.PdfPrint";
        private const string SettingsButtonName = "GrdRevit.Settings";
        private const string ViewsManagerButtonName = "GrdRevit.ViewsManager";
        private const string DxfMapButtonName = "GrdRevit.DxfMap";
        private const string SuperRotateButtonName = "GrdRevit.SuperRotate";
        private const string RadiatorTagsButtonName = "GrdRevit.RadiatorTags";
        private const string CopyRoomsButtonName = "GrdRevit.CopyRooms";
        private const string TomatImportButtonName = "GrdRevit.TomatImport";
        private const string SelectInsulationButtonName = "GrdRevit.SelectInsulation";
        private const string SystemTypesButtonName = "GrdRevit.SystemTypes";

        public Result OnStartup(UIControlledApplication app)
        {
            GrdLog.LogSessionStart();
            GrdLog.Log("OnStartup: begin");
            try
            {
                app.CreateRibbonTab(TabName);

                var panel = app.CreateRibbonPanel(TabName, PanelName);

                var data = new PushButtonData(
                    ButtonDataName,
                    "Применить данные\nиз Audytor",
                    Assembly.GetExecutingAssembly().Location,
                    typeof(GrdLoaderCommand).FullName)
                {
                    ToolTip = "Заполнить таблицу приборов (room-radiator.txt), сопоставить настройки клапанов и применить тип к выделенному оборудованию",
                    LongDescription = "Загружает помещения и радиаторы из room-radiator.txt, настройки клапанов из valve-settings.txt, " +
                                      "заполняет колонку «Настройка клапана» по ключу «помещение + мощность». " +
                                      "При выбранном единичном механическом оборудовании кнопка «Применить» в строке " +
                                      "применяет тип семейства и параметры экземпляра из настроек плагина."
                };

                data.Image = LoadIcon("GrdRevit.Resources.plugin16.png");
                data.LargeImage = LoadIcon("GrdRevit.Resources.plugin32.png");
                data.AvailabilityClassName = typeof(GrdAvailability).FullName;

                panel.AddItem(data);

                var familyData = new PushButtonData(
                    FamilyParamsButtonName,
                    "Параметры\nсемейств",
                    Assembly.GetExecutingAssembly().Location,
                    typeof(GrdFamilyParamsCommand).FullName)
                {
                    ToolTip = "Добавить, удалить или изменить привязку (экземпляр/тип) параметров сразу у нескольких семейств активного документа",
                    LongDescription = "Открывает окно: выберите семейства, добавьте операции «добавить/удалить/изменить привязку» для обычных или общих параметров, " +
                                      "укажите тип значения и группу, затем нажмите «Применить». " +
                                      "Операция «Изменить привязку» переключает существующий параметр между экземпляром и типом. " +
                                      "Файл общих параметров запоминается и восстанавливается при следующем запуске."
                };
                familyData.Image = LoadIcon("GrdRevit.Resources.familyparams16.png");
                familyData.LargeImage = LoadIcon("GrdRevit.Resources.familyparams32.png");
                familyData.AvailabilityClassName = typeof(GrdAvailability).FullName;

                panel.AddItem(familyData);

                var box3dData = new PushButtonData(
                    Box3DButtonName,
                    "3D-фрагмент\nиз области",
                    Assembly.GetExecutingAssembly().Location,
                    typeof(GrdBox3DCommand).FullName)
                {
                    ToolTip = "Создать 3D-вид-фрагмент по растянутой на плане области с высотой по умолчанию из настроек",
                    LongDescription = "Нажмите кнопку, затем растяните прямоугольную область мышью на плане этажа. " +
                                      "Плагин создаст изометрический 3D-вид с границами точно по этой области. " +
                                      "Высота объёма берётся из настроек плагина (по умолчанию 3 м) — " +
                                      "изменить можно в отдельной кнопке «Настройки»."
                };
                box3dData.Image = LoadIcon("GrdRevit.Resources.box3d16.png");
                box3dData.LargeImage = LoadIcon("GrdRevit.Resources.box3d32.png");
                box3dData.AvailabilityClassName = typeof(GrdAvailability).FullName;

                panel.AddItem(box3dData);

                var boxPlanData = new PushButtonData(
                    BoxPlanButtonName,
                    "План-фрагмент\nиз 3D",
                    Assembly.GetExecutingAssembly().Location,
                    typeof(GrdBoxPlanCommand).FullName)
                {
                    ToolTip = "Создать план-фрагмент по выделенным на 3D-виде объектам: план самого низкого уровня, обрезанный по выделению",
                    LongDescription = "Выделите объекты на 3D-виде и нажмите кнопку. " +
                                      "Плагин определит самый низкий уровень среди объектов (по признаку уровня элементов и геометрии), " +
                                      "создаст фрагмент этажного плана этого уровня, обрежет его точно по габаритам выделения " +
                                      "и сразу откроет. Пределы видимости по высоте охватывают всё выделение."
                };
                boxPlanData.Image = LoadIcon("GrdRevit.Resources.boxplan16.png");
                boxPlanData.LargeImage = LoadIcon("GrdRevit.Resources.boxplan32.png");
                boxPlanData.AvailabilityClassName = typeof(GrdAvailability).FullName;

                panel.AddItem(boxPlanData);

                var instanceData = new PushButtonData(
                    InstanceParamsButtonName,
                    "Параметры\nэкземпляра",
                    Assembly.GetExecutingAssembly().Location,
                    typeof(GrdInstanceParamsCommand).FullName)
                {
                    ToolTip = "Изменить значения всех параметров выбранного экземпляра семейства без открытия редактора семейства",
                    LongDescription = "Выделите один экземпляр семейства в проекте и нажмите кнопку. " +
                                      "Откроется окно со всеми параметрами элемента и типа: параметры экземпляра " +
                                      "изменяются только у выбранного элемента, параметры типа — у типа, то есть " +
                                      "у всех экземпляров этого типа. Значения редактируются прямо в таблице."
                };
                instanceData.Image = LoadIcon("GrdRevit.Resources.params16.png");
                instanceData.LargeImage = LoadIcon("GrdRevit.Resources.params32.png");
                instanceData.AvailabilityClassName = typeof(GrdAvailability).FullName;

                panel.AddItem(instanceData);

                var copyData = new PushButtonData(
                    CopyParamsButtonName,
                    "Скопировать\nпараметры",
                    Assembly.GetExecutingAssembly().Location,
                    typeof(GrdCopyParamsCommand).FullName)
                {
                    ToolTip = "Перенести параметры (и значения типов) из одного семейства в другое без открытия редактора семейства",
                    LongDescription = "Выберите семейство-источник и целевое семейство (с поиском и режимом «выбранные/спецификация»). " +
                                      "Отметьте параметры слева и нажмите «Скопировать»: отсутствующие в целевом семействе параметры " +
                                      "добавляются с такой же привязкой (экземпляр/тип), при иной привязке — заменяются, значения " +
                                      "одноимённых типов копируются. Параметры целевого с формулами подсвечиваются и имеют кнопку «снять формулу»."
                };
                copyData.Image = LoadIcon("GrdRevit.Resources.copyparams16.png");
                copyData.LargeImage = LoadIcon("GrdRevit.Resources.copyparams32.png");
                copyData.AvailabilityClassName = typeof(GrdAvailability).FullName;

                panel.AddItem(copyData);

                var viewsData = new PushButtonData(
                    ViewsManagerButtonName,
                    "Управляющий\nвидами",
                    Assembly.GetExecutingAssembly().Location,
                    typeof(GrdViewsManagerCommand).FullName)
                {
                    ToolTip = "Список всех видов документа с поиском и фильтрами: переименовать, дублировать, перейти к виду",
                    LongDescription = "Открывает окно со всеми видами активного документа. " +
                                      "Фильтры «Разрезы / Планы / 3D» и поиск по имени. " +
                                      "Переименование — двойной щелчок или F2 по имени вида, " +
                                      "кнопка «Копия» дублирует вид и сразу открывает его имя, " +
                                      "кнопка «Открыть» переключает Revit на этот вид."
                };
                viewsData.Image = LoadIcon("GrdRevit.Resources.views16.png");
                viewsData.LargeImage = LoadIcon("GrdRevit.Resources.views32.png");
                viewsData.AvailabilityClassName = typeof(GrdAvailability).FullName;

                panel.AddItem(viewsData);

                var scheduleData = new PushButtonData(
                    ScheduleExportButtonName,
                    "Экспорт\nспецификации",
                    Assembly.GetExecutingAssembly().Location,
                    typeof(GrdScheduleExportCommand).FullName)
                {
                    ToolTip = "Выгрузить текущую спецификацию в файл LibreOffice Calc (.ods) и открыть папку с файлом",
                    LongDescription = "Открывает спецификацию (вид таблицы) и нажимает кнопку — плагин сохраняет её содержимое в формат " +
                                      "LibreOffice Calc (.ods). Последние папка и имя файла запоминаются и подставляются в " +
                                      "следующий раз. После сохранения открывается Проводник с выделенным файлом (сам файл не открывается)."
                };
                scheduleData.Image = LoadIcon("GrdRevit.Resources.schedule16.png");
                scheduleData.LargeImage = LoadIcon("GrdRevit.Resources.schedule32.png");
                scheduleData.AvailabilityClassName = typeof(GrdAvailability).FullName;

                panel.AddItem(scheduleData);

                var snapData = new PushButtonData(
                    ScheduleSnapshotButtonName,
                    "Снимок\nспецификаций",
                    Assembly.GetExecutingAssembly().Location,
                    typeof(GrdScheduleSnapshotCommand).FullName)
                {
                    ToolTip = "Спецификации с листов в редактируемой таблице: правки, возврат к виду из модели, печать в PDF",
                    LongDescription = "Открывает окно: слева — листы проекта со спецификациями (поиск по номеру/имени листа и имени " +
                                      "спецификации), справа — выбранная спецификация в редактируемой таблице. " +
                                      "Правки хранятся только в окне и не попадают в модель; «Обновить из модели» возвращает " +
                                      "марки, наименования и количество к исходному виду; «Печать в PDF» сохраняет таблицу (с правками) в PDF."
                };
                snapData.Image = LoadIcon("GrdRevit.Resources.schedulesnap16.png");
                snapData.LargeImage = LoadIcon("GrdRevit.Resources.schedulesnap32.png");
                snapData.AvailabilityClassName = typeof(GrdAvailability).FullName;

                panel.AddItem(snapData);

                var pdfPrintData = new PushButtonData(
                    PdfPrintButtonName,
                    "Печать\nв PDF",
                    Assembly.GetExecutingAssembly().Location,
                    typeof(GrdPdfPrintCommand).FullName)
                {
                    ToolTip = "Сохранить выбранные листы в один PDF: размер страницы подбирается по каждому листу",
                    LongDescription = "Открывает окно со списком всех листов проекта (поиск, галочки). " +
                                      "Отмеченные листы сохраняются в один PDF-файл; размер страницы берётся из каждого листа. " +
                                      "Векторный вывод компактнее, растровый — надёжно передаёт знак «Ø», если векторное ядро его искажает."
                };
                pdfPrintData.Image = LoadIcon("GrdRevit.Resources.schedulesnap16.png");
                pdfPrintData.LargeImage = LoadIcon("GrdRevit.Resources.schedulesnap32.png");
                pdfPrintData.AvailabilityClassName = typeof(GrdAvailability).FullName;

                panel.AddItem(pdfPrintData);

                var superRotateData = new PushButtonData(
                    SuperRotateButtonName,
                    "Супер\nповорот",
                    Assembly.GetExecutingAssembly().Location,
                    typeof(GrdSuperRotateCommand).FullName)
                {
                    ToolTip = "Упрощённый поворот: кликом выберите элемент, затем кликом укажите центр — угол берётся последний запомненный",
                    LongDescription = "Запустите команду, щёлкните по элементу, который нужно повернуть, " +
                                      "затем щёлкните в точку центра поворота — элемент повернётся вокруг неё. " +
                                      "Поворачивается один выбранный элемент. " +
                                      "Угол всегда берётся последний запомненный (при первом запуске запрашивается один раз " +
                                      "и сохраняется в настройках; положительное значение — против часовой стрелки). " +
                                      "Центр поворота — это точная позиция второго щелчка мыши."
                };
                superRotateData.Image = LoadIcon("GrdRevit.Resources.rotate16.png");
                superRotateData.LargeImage = LoadIcon("GrdRevit.Resources.rotate32.png");
                superRotateData.AvailabilityClassName = typeof(GrdAvailability).FullName;

                panel.AddItem(superRotateData);

                var dxfMapData = new PushButtonData(
                    DxfMapButtonName,
                    "DXF-план\nс приборами",
                    Assembly.GetExecutingAssembly().Location,
                    typeof(GrdDxfMapCommand).FullName)
                {
                    ToolTip = "Отрисовка DXF-плана: выбрать марку прибора кликом/рамкой и применить её к элементу в Revit. Кнопка «Настройки марок» открывает сопоставление марок с семействами",
                    LongDescription = "Открывает окно с чертежом из DXF (pan/зум/выделение рамкой). " +
                                      "Кликом или рамкой выберите прибор на плане; «Применить к выделенному» работает с " +
                                      "текущим выделением в Revit, режим «Применить к Revit» применяет марку к тому элементу, " +
                                      "который вы кликните на виде Revit после выбора прибора на плане. " +
                                      "Кнопка «Настройки марок…» открывает сопоставление «марка DXF -> семейство/тип Revit»."
                };
                dxfMapData.Image = LoadIcon("GrdRevit.Resources.dxfmap16.png");
                dxfMapData.LargeImage = LoadIcon("GrdRevit.Resources.dxfmap32.png");
                dxfMapData.AvailabilityClassName = typeof(GrdAvailability).FullName;

                panel.AddItem(dxfMapData);

                var radiatorTagsData = new PushButtonData(
                    RadiatorTagsButtonName,
                    "Расставить тэги\nпо границе",
                    Assembly.GetExecutingAssembly().Location,
                    typeof(GrdRadiatorTagsCommand).FullName)
                {
                    ToolTip = "Выровнять выбранные тэги (например, радиаторов) вдоль начерченной линии-границы: " +
                              "тэги встают рядами по выбранной стороне, при нехватке места — несколькими колонками",
                    LongDescription = "Взаимодействие: 1) заранее выберите тэги на виде; 2) запустите команду; " +
                                      "3) начертите линию-границу двумя кликами (как «Ось зеркала»); " +
                                      "4) кликните с той стороны линии, куда сдвинуть тэги. Дальше тэги " +
                                      "расставляются рядами вдоль линии: каждый тэг — напротив своего якоря, " +
                                      "а при нехватке места по длине — укладываются во внутренние ряды-колонки. " +
                                      "Прямая выноска к якорю сохраняется; при пересечении чужого текста " +
                                      "позиция тэга в ряду подбирается так, чтобы выноска шла мимо. " +
                                      "Результат пишется в лог."
                };
                radiatorTagsData.Image = LoadIcon("GrdRevit.Resources.arrange16.png");
                radiatorTagsData.LargeImage = LoadIcon("GrdRevit.Resources.arrange32.png");
                radiatorTagsData.AvailabilityClassName = typeof(GrdAvailability).FullName;

                panel.AddItem(radiatorTagsData);

                var copyRoomsData = new PushButtonData(
                    CopyRoomsButtonName,
                    "Скопировать\nкомнаты",
                    Assembly.GetExecutingAssembly().Location,
                    typeof(GrdCopyRoomsCommand).FullName)
                {
                    ToolTip = "Создать пространства ОВ по помещениям выбранной связи АР: имя и номер переносятся как есть",
                    LongDescription = "Находит помещения в связанном файле АР и создаёт для каждого пространство ОВ с " +
                                      "такими же именем и номером в той же позиции на плане. " +
                                      "Если связь одна — она используется автоматически; если связей несколько — " +
                                      "кликните по нужной связи на виде. Уровень и фаза подбираются по имени " +
                                      "(при расхождении — ближайший уровень по высоте и фаза по умолчанию). " +
                                      "Итог — в окне результата и в логе."
                };
                copyRoomsData.Image = LoadIcon("GrdRevit.Resources.copyrooms16.png");
                copyRoomsData.LargeImage = LoadIcon("GrdRevit.Resources.copyrooms32.png");
                copyRoomsData.AvailabilityClassName = typeof(GrdAvailability).FullName;

                panel.AddItem(copyRoomsData);

                var tomatData = new PushButtonData(
                    TomatImportButtonName,
                    "Импорт .tomat",
                    Assembly.GetExecutingAssembly().Location,
                    typeof(GrdTomatImportCommand).FullName)
                {
                    ToolTip = "Записать приточные/вытяжные системы и расходы воздуха из файла .tomat в параметры пространств ОВ (без создания пространств)",
                    LongDescription = "Открывает файл .tomat (программа расчёта теплопотерь/отопления) и сопоставляет " +
                                      "его помещения с существующими пространствами активного документа по номеру и имени. " +
                                      "В окне указываются четыре параметра пространства: приточная и вытяжная система (текст), " +
                                      "расход приточного и вытяжного воздуха (число, м³/ч). " +
                                      "Несопоставленные строки подсвечиваются — их можно сопоставить вручную в таблице. " +
                                      "Имена параметров и путь к файлу запоминаются."
                };
                tomatData.Image = LoadIcon("GrdRevit.Resources.tomat16.png");
                tomatData.LargeImage = LoadIcon("GrdRevit.Resources.tomat32.png");
                tomatData.AvailabilityClassName = typeof(GrdAvailability).FullName;

                panel.AddItem(tomatData);

                var selectInsulationData = new PushButtonData(
                    SelectInsulationButtonName,
                    "Выбрать\nизоляцию",
                    Assembly.GetExecutingAssembly().Location,
                    typeof(GrdSelectInsulationCommand).FullName)
                {
                    ToolTip = "Режим выделения только изоляции труб и воздуховодов — щелчком или рамкой, без выбора трубы и «Редактировать изоляцию»",
                    LongDescription = "Запустите команду и щёлкайте по трубам, воздуховодам и фитингам с изоляцией " +
                                      "или выделяйте их рамкой (окно/секущая) — рамка захватывает только элементы, " +
                                      "у которых изоляция есть. По «Готово»/Enter в активное выделение подставляется " +
                                      "именно изоляция (сама труба не выделяется), после чего доступны «Редактировать " +
                                      "изоляцию» и смена типа. Esc — отмена. Решает проблему Revit, где изоляцию " +
                                      "можно выделить только через трубу и «Редактировать изоляцию»."
                };
                selectInsulationData.Image = LoadIcon("GrdRevit.Resources.insulation16.png");
                selectInsulationData.LargeImage = LoadIcon("GrdRevit.Resources.insulation32.png");
                selectInsulationData.AvailabilityClassName = typeof(GrdAvailability).FullName;

                panel.AddItem(selectInsulationData);

                var systemTypesData = new PushButtonData(
                    SystemTypesButtonName,
                    "Типы\nсистем",
                    Assembly.GetExecutingAssembly().Location,
                    typeof(GrdSystemTypesCommand).FullName)
                {
                    ToolTip = "Типы систем труб и воздуховодов: имя, сокращение, цвет, толщина и тип линий, легенда, экспорт в Excel",
                    LongDescription = "Таблица типов систем активного документа (трубы или воздуховоды) с редактируемыми " +
                                      "ячейками: имя, сокращение, цвет, толщина линий и тип линий. Правки подсвечиваются " +
                                      "жёлтым и применяются к модели кнопкой «Сохранить изменения» (затем доступны и в " +
                                      "штатном диалоге Revit). «Копировать» создаёт копию типа с именем «Имя (копия)». " +
                                      "«Создать легенду» рисует в активном виде вертикальный стек линий длиной 1000 мм " +
                                      "с цветом и типом линий из типа и подписью «Имя (Сокращение)» справа. «Экспорт в " +
                                      "Excel» выгружает текущий набор в книгу .xlsx без внешних зависимостей."
                };
                systemTypesData.Image = LoadIcon("GrdRevit.Resources.systemtypes16.png");
                systemTypesData.LargeImage = LoadIcon("GrdRevit.Resources.systemtypes32.png");
                systemTypesData.AvailabilityClassName = typeof(GrdAvailability).FullName;

                panel.AddItem(systemTypesData);

                var settingsData = new PushButtonData(
                    SettingsButtonName,
                    "Настройки",
                    Assembly.GetExecutingAssembly().Location,
                    typeof(GrdSettingsCommand).FullName)
                {
                    ToolTip = "Настройки всех функций плагина JTOOLS",
                    LongDescription = "Открывает единое окно настроек: карта значений «прибор -> параметр экземпляра», " +
                                      "сопоставление кода прибора с именем типа семейства и высота 3D-вида-фрагмента по умолчанию."
                };
                settingsData.Image = LoadIcon("GrdRevit.Resources.settings16.png");
                settingsData.LargeImage = LoadIcon("GrdRevit.Resources.settings32.png");
                settingsData.AvailabilityClassName = typeof(GrdAvailability).FullName;

                panel.AddItem(settingsData);

                GrdLog.Log("OnStartup: done");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                GrdLog.Log("OnStartup: EXCEPTION: " + ex);
                TaskDialog.Show(ProductName + ": ошибка запуска", ex.Message);
                return Result.Failed;
            }
        }

        /// <summary>Значок кнопки из встроенного ресурса сборки (PNG).</summary>
        private static ImageSource LoadIcon(string resourceName)
        {
            try
            {
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                    {
                        GrdLog.Log("LoadIcon: не найден ресурс " + resourceName);
                        return null;
                    }
                    var decoder = new PngBitmapDecoder(stream,
                        BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    return decoder.Frames[0];
                }
            }
            catch (Exception ex)
            {
                GrdLog.Log("LoadIcon: EXCEPTION " + ex);
                return null;
            }
        }

        public Result OnShutdown(UIControlledApplication app)
        {
            return Result.Succeeded;
        }
    }

    public class GrdAvailability : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication applicationData, Autodesk.Revit.DB.CategorySet selectedCategories)
        {
            return true;
        }
    }
}