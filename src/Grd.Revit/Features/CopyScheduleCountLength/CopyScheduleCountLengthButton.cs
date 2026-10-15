using Autodesk.Revit.UI;

namespace GrdRevit
{
    public partial class GrdApplication
    {
        public static string CopyScheduleCountLengthButtonName = "GrdRevit.CopyScheduleCountLength";

        private void CreateCopyScheduleCountLengthButton(RibbonPanel panel)
        {
            try
            {
                var data = new PushButtonData(CopyScheduleCountLengthButtonName,
                    "Count/Length\nв параметр",
                    System.Reflection.Assembly.GetExecutingAssembly().Location,
                    typeof(GrdCopyScheduleCountLengthCommand).FullName)
                {
                    ToolTip = "Мультикатегорийная спецификация: значения выбранной колонки построчно записываются в выбранный текстовый параметр элементов каждой строки.",
                    AvailabilityClassName = typeof(CopyScheduleCountLengthAvail).FullName,
                    LargeImage = LoadIcon("GrdRevit.Resources.countlength32.png"),
                    Image = LoadIcon("GrdRevit.Resources.countlength16.png")
                };
                panel.AddItem(data);
            }
            catch { }
        }
    }
}