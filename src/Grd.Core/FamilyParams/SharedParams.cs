namespace GrdRevit.Core
{
    /// <summary>Определение общего параметра из файла общих параметров Revit (.txt).</summary>
    public sealed class SharedParamDef
    {
        public string Name = string.Empty;
        public string Guid = string.Empty;
        public string Group = string.Empty;
        public string StorageType = string.Empty;
    }

    /// <summary>Источник параметра при добавлении в семейство: обычный или общий (shared).</summary>
    public enum ParamSourceKind
    {
        Family = 0,
        Shared = 1
    }

    /// <summary>Операция над семейством: добавить или удалить параметр.</summary>
    public sealed class FamilyParamOp
    {
        public string Name = string.Empty;
        public ParamSourceKind Source = ParamSourceKind.Family;
        public string StorageType = "Текст";
        public string Group = "Данные";
        public bool IsInstance = true;
        public bool Remove;
        public string Status = "Добавить";
        public string SharedGuid = string.Empty; // для общих: GUID определения (пусто = создать новый)
    }
}