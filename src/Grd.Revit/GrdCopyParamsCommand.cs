using System;
using System.Diagnostics;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using GrdRevit.Ui;

namespace GrdRevit
{
    /// <summary>
    /// Открывает окно «Скопировать параметры»: перенос параметров (и значений)
    /// из семейства-источника в целевое семейство активного документа.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class GrdCopyParamsCommand : IExternalCommand
    {
        private static CopyParamsWindow _window;

        public Result Execute(ExternalCommandData commandData, ref string message, Autodesk.Revit.DB.ElementSet elements)
        {
            var sw = Stopwatch.StartNew();
            GrdLog.Log("CP0: Execute start");
            try
            {
                RevitContext.Initialize(commandData.Application);
                GrdLog.Log("CP1: after Initialize");

                if (_window == null || !_window.IsVisible)
                {
                    var window = new CopyParamsWindow();
                    window.Closed += (s, e) => _window = null;
                    GrdLog.Log("CP2: CopyParamsWindow created");

                    try
                    {
                        var owner = MainWindow.Instance;
                        if (owner != null && owner.IsVisible)
                        {
                            window.Owner = owner;
                            GrdLog.Log("CP2b: owner = главное окно плагина");
                        }
                    }
                    catch { }

                    window.Show();
                    GrdLog.Log("CP3: window shown");
                }
                else
                {
                    WindowRestore.Activate(_window);
                    GrdLog.Log("CP3b: window activated");
                }

                GrdLog.Log("CP4: succeeded in " + sw.ElapsedMilliseconds + " ms");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                GrdLog.Log("CP99: EXCEPTION: " + ex);
                try
                {
                    System.Windows.MessageBox.Show(ex.Message, "JTOOLS: скопировать параметры",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                }
                catch { }
                return Result.Failed;
            }
        }
    }
}
