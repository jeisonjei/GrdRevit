using System;
using System.Diagnostics;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using GrdRevit.Ui;

namespace GrdRevit
{
    /// <summary>
    /// Открывает окно «Параметры семейств»: добавление/удаление параметров
    /// (обычных и общих) сразу у нескольких семейств активного документа.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class GrdFamilyParamsCommand : IExternalCommand
    {
        private static FamilyParamsWindow _window;

        public Result Execute(ExternalCommandData commandData, ref string message, Autodesk.Revit.DB.ElementSet elements)
        {
            var sw = Stopwatch.StartNew();
            GrdLog.Log("F0: Execute start");
            try
            {
                GrdLog.Log("F1: before Initialize");
                RevitContext.Initialize(commandData.Application);
                GrdLog.Log("F2: after Initialize");

                if (_window == null || !_window.IsVisible)
                {
                    var window = new FamilyParamsWindow();
                    window.Closed += (s, e) => _window = null;
                    GrdLog.Log("F3: FamilyParamsWindow created");

                    // Владение задаём до Show(), чтобы при закрытии окна активация
                    // возвращалась Revit, а не «сворачивала» его в панель задач.
                    try
                    {
                        var owner = MainWindow.Instance;
                        if (owner != null && owner.IsVisible)
                        {
                            window.Owner = owner;
                            GrdLog.Log("F3b: owner = главное окно плагина");
                        }
                    }
                    catch { }

                    window.Show();
                    GrdLog.Log("F4: window shown");
                }
                else
                {
                    _window.Activate();
                    GrdLog.Log("F4b: window activated");
                }

                GrdLog.Log("F5: succeeded in " + sw.ElapsedMilliseconds + " ms");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                GrdLog.Log("F99: EXCEPTION: " + ex);
                try
                {
                    System.Windows.MessageBox.Show(ex.Message, "JTOOLS: параметры семейств",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                }
                catch { }
                return Result.Failed;
            }
        }
    }
}