using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace GrdRevit
{
    /// <summary>
    /// Availability: активна только при открытой мультикатегорийной спецификации (ViewSchedule, IsMultiCategory)
    /// </summary>
    public class CopyScheduleCountLengthAvail : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication app, Autodesk.Revit.DB.CategorySet categories)
        {
            try
            {
                var uidoc = app?.ActiveUIDocument;
                if (uidoc == null) return false;
                return uidoc.ActiveView is ViewSchedule;
            }
            catch
            {
                return false;
            }
        }
    }
}