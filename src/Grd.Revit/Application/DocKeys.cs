using Autodesk.Revit.DB;

namespace GrdRevit.Revit
{
    /// <summary>Единый ключ документа для хранения данных плагина (путь или название):
    /// избранные и правки запоминаются для каждого документа отдельно.</summary>
    public static class DocKeys
    {
        public static string Get(Document doc)
        {
            if (doc == null) return string.Empty;
            try
            {
                if (!string.IsNullOrEmpty(doc.PathName)) return doc.PathName;
                return (doc.Title ?? string.Empty) + " (без документа)";
            }
            catch { return string.Empty; }
        }
    }
}