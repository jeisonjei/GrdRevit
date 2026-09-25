using System;
using System.Windows;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using GrdRevit.Ui;

namespace GrdRevit
{
    /// <summary>Открывает окно «DXF — план отопления с приборами».</summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class GrdDxfMapCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, Autodesk.Revit.DB.ElementSet elements)
        {
            try
            {
                RevitContext.Initialize(commandData.Application);

                var window = DxfMapWindow.Instance;
                if (window == null)
                {
                    window = new DxfMapWindow();
                    window.Closed += (s, e) => DxfMapWindow.Instance = null;
                }

                if (!window.IsVisible)
                {
                    new WindowInteropHelper(window)
                    {
                        Owner = RevitContext.MainWindowHandle
                    };
                    window.Show();
                    DxfMapWindow.Instance = window;
                }
                else
                {
                    window.Activate();
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                GrdLog.Log("GrdDxfMapCommand: EXCEPTION: " + ex);
                try
                {
                    MessageBox.Show(ex.Message, "JTOOLS: ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch { }
                return Result.Failed;
            }
        }
    }
}