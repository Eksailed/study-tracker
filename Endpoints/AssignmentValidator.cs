namespace StudyTracker;

public static class AssignmentValidator
{
    public static string? Validate(AssignmentInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Title) || input.Title.Trim().Length > 140)
            return "Введите название задания от 1 до 140 символов.";
        if (input.Description?.Length > 2000) return "Описание должно быть не длиннее 2000 символов.";
        if (!Enum.IsDefined(input.Status)) return "Выберите допустимый статус.";
        if (input.DueDate < new DateOnly(2000, 1, 1) || input.DueDate > new DateOnly(2100, 12, 31))
            return "Выберите срок между 2000 и 2100 годом.";
        return null;
    }
}
