namespace EntregasApi.Services;

/// <summary>
/// Calcula la semana operativa de una tanda usando UTC y ventanas de 7 dias
/// desde la fecha de inicio.
/// </summary>
public static class TandaWeekCalculator
{
    /// <summary>
    /// Devuelve la semana actual de la tanda. La semana 2 empieza exactamente
    /// 7 dias despues de StartDate.
    /// </summary>
    public static int CalculateCurrentWeek(DateTime startDate, DateTime? utcNow = null)
    {
        var today = (utcNow ?? DateTime.UtcNow).Date;
        var days = (today - startDate.Date).Days;
        if (days <= 0) return 1;
        return (days / 7) + 1;
    }

    /// <summary>
    /// Devuelve la semana actual limitada al rango de semanas configuradas.
    /// </summary>
    public static int CalculateClampedCurrentWeek(
        DateTime startDate,
        int totalWeeks,
        DateTime? utcNow = null)
    {
        if (totalWeeks < 1) return 1;
        return Math.Clamp(CalculateCurrentWeek(startDate, utcNow), 1, totalWeeks);
    }
}
