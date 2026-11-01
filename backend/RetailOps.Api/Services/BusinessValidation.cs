using System.Globalization;
using System.Net.Mail;
using RetailOps.Api.Security;

namespace RetailOps.Api.Services;

internal static class BusinessValidation
{
    public const int QuantityLimit = 1_000_000_000;
    public const decimal MoneyLimit = 999_999_999_999.99m;

    public static string Text(string field, string? value, int maximum, Dictionary<string, string[]> errors, bool required = true, bool multiline = false)
    {
        var result = value?.Trim() ?? "";
        if (required && result.Length == 0) errors[field] = ["This field is required."];
        else if (result.Length > maximum) errors[field] = [$"Use at most {maximum} characters."];
        else if (result.Any(c => char.IsControl(c) && !(multiline && c is '\r' or '\n' or '\t')))
            errors[field] = ["Remove unsupported control characters."];
        return result;
    }

    public static void Money(string field, decimal value, Dictionary<string, string[]> errors)
    {
        if (value < 0 || value > MoneyLimit || decimal.Round(value, 2) != value)
            errors[field] = ["Enter a nonnegative amount with at most two decimal places (maximum 999999999999.99)."];
    }

    public static void PositiveQuantity(string field, int quantity, Dictionary<string, string[]> errors)
    {
        if (quantity < 1 || quantity > QuantityLimit) errors[field] = [$"Enter a whole quantity from 1 to {QuantityLimit}."];
    }

    public static void ThrowIfInvalid(Dictionary<string, string[]> errors)
    {
        if (errors.Count > 0) throw new AppException(400, "Invalid request", "Correct the highlighted fields and try again.", errors);
    }

    public static void Email(string value, Dictionary<string, string[]> errors)
    {
        if (value.Length > 0 && (!MailAddress.TryCreate(value, out var address) || address.Address != value))
            errors["email"] = ["Enter a valid contact email address."];
    }

    public static (int Page, int PageSize) Pagination(HttpRequest request)
    {
        return (PositiveQueryInt(request, "page", 1, int.MaxValue), PositiveQueryInt(request, "pageSize", 20, 100));
    }

    private static int PositiveQueryInt(HttpRequest request, string name, int defaultValue, int maximum)
    {
        var raw = request.Query[name].ToString();
        if (raw.Length == 0) return defaultValue;
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 1 || value > maximum)
            throw new AppException(400, "Invalid request", $"{name} must be between 1 and {maximum}.", new() { [name] = [$"Enter a whole number between 1 and {maximum}."] });
        return value;
    }

    public static int Skip(int page, int pageSize)
    {
        if ((long)(page - 1) * pageSize > int.MaxValue)
            throw new AppException(400, "Invalid request", "The requested page is too large.");
        return (page - 1) * pageSize;
    }

    public static string Search(HttpRequest request)
    {
        var search = request.Query["search"].ToString().Trim();
        if (search.Length > 200) throw new AppException(400, "Invalid request", "Search must contain at most 200 characters.");
        return search;
    }

    public static string Like(string value) => "%" + value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    public static Guid? GuidQuery(HttpRequest request, string name)
    {
        var raw = request.Query[name].ToString();
        if (raw.Length == 0) return null;
        if (!Guid.TryParse(raw, out var id) || id == Guid.Empty)
            throw new AppException(400, "Invalid request", $"{name} must be a valid identifier.", new() { [name] = ["Choose a valid record."] });
        return id;
    }

    public static bool BooleanQuery(HttpRequest request, string name)
    {
        var raw = request.Query[name].ToString();
        if (raw.Length == 0) return false;
        if (!bool.TryParse(raw, out var value))
            throw new AppException(400, "Invalid request", $"{name} must be true or false.");
        return value;
    }
}
