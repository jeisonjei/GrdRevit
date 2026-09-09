using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using GrdRevit.Ui;

namespace GrdRevit
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class GrdLoaderCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, Autodesk.Revit.DB.ElementSet elements)
        {
            var sw = Stopwatch.StartNew();
            // Watchdog: если главный поток Revit застрянет, фоновый поток
            // продолжит писать в журнал — будет видно, что это дедлок.
            var watchdog = new System.Threading.Thread(() =>
            {
                for (int i = 1; i <= 20; i++)
                {
                    System.Threading.Thread.Sleep(5000);
                    GrdLog.Log("WATCHDOG tick " + i + ": Execute not finished after " + (i * 5) + "s");
                }
            });
            watchdog.IsBackground = true;
            watchdog.Start();
            GrdLog.Log("S0: Execute start");
            try
            {
                GrdLog.Log("S0a: accessing commandData.Application");
                var app = commandData.Application;
                GrdLog.Log("S0b: app obtained: " + (app != null ? "notnull" : "NULL"));

                GrdLog.Log("S1: before Initialize");
                RevitContext.Initialize(app);
                GrdLog.Log("S2: after Initialize; LastGrdPath='" + RevitContext.Settings.LastGrdPath + "'");

                var window = MainWindow.Instance;
                GrdLog.Log("S3: Instance=" + (window != null ? "present" : "null") + ", visible=" + (window != null && window.IsVisible));

                if (window == null)
                {
                    GrdLog.Log("S4: creating MainWindow");
                    window = new MainWindow();
                    window.Closed += (s, e) => MainWindow.Instance = null;
                    GrdLog.Log("S5: MainWindow created");
                }

                if (!window.IsVisible)
                {
                    GrdLog.Log("S6: parenting to Revit main window");
                    var revitHandle = RevitContext.MainWindowHandle;
                    // Владение (owner) обязательно задаём ДО Show(): при закрытии окна
                    // Windows возвращает активацию владельцу, а не «сворачивает» Revit.
                    new WindowInteropHelper(window)
                    {
                        Owner = revitHandle
                    };
                    GrdLog.Log("S6b: owner set to 0x" + revitHandle.ToString("X"));
                    GrdLog.Log("S7: Show()");
                    window.Show();
                    MainWindow.Instance = window;
                    GrdLog.Log("S8: Show() returned, window shown");
                }
                else
                {
                    window.Activate();
                    GrdLog.Log("S9: Activate() done");
                }

                GrdLog.Log("S10: succeeded in " + sw.ElapsedMilliseconds + " ms");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                GrdLog.Log("S99: EXCEPTION: " + ex);
                MainWindow.Instance = null;
                try
                {
                    MessageBox.Show(ex.Message, "ГрД: ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch { }
                return Result.Failed;
            }
            finally
            {
                GC.KeepAlive(watchdog);
            }
        }
    }
}