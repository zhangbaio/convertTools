using System.Globalization;

namespace LicenseTranslate.Core;

internal sealed class BusinessLicenseSheet
{
    public string Credit { get; init; } = "";
    public string Name { get; init; } = "";
    public string Type { get; init; } = "";
    public string ExtraLabel { get; init; } = "";
    public string ExtraValue { get; init; } = "";
    public string DateLabel { get; init; } = "";
    public string Established { get; init; } = "";
    public string PersonLabel { get; init; } = "";
    public string Person { get; init; } = "";
    public string AddressLabel { get; init; } = "";
    public string Address { get; init; } = "";
    public string Scope { get; init; } = "";
    public string Authority { get; init; } = "";
    public string Issued { get; init; } = "";
    public string Duplicate { get; init; } = "";
    public string TranslatedOn { get; init; } = "";

    public static BusinessLicenseSheet From(BusinessLicenseDraft draft)
    {
        var individual = draft.Kind == "个体工商户";
        var authority = draft.EnglishAuthority.Trim();
        if (authority.Length > 0 && !authority.EndsWith("(Sealed)", StringComparison.Ordinal))
            authority += " (Sealed)";
        var duplicateNo = string.IsNullOrWhiteSpace(draft.EnglishDuplicateNo) ? draft.DuplicateNo.Trim() : draft.EnglishDuplicateNo.Trim();
        return new BusinessLicenseSheet
        {
            Credit = draft.CreditCode.Trim(),
            Name = draft.EnglishName.Trim(),
            Type = draft.EnglishType.Trim(),
            ExtraLabel = individual ? "Form of Organization" : "Registered capital",
            ExtraValue = draft.EnglishExtraValue.Trim(),
            DateLabel = individual ? "Registration Date" : "Date of establishment",
            Established = draft.EnglishDateValue.Trim(),
            PersonLabel = "Legal representative",
            Person = draft.EnglishPersonName.Trim(),
            AddressLabel = "Address",
            Address = draft.EnglishAddress.Trim(),
            Scope = draft.EnglishScope.Trim(),
            Authority = authority,
            Issued = string.IsNullOrWhiteSpace(draft.EnglishIssueDate) ? draft.EnglishDateValue.Trim() : draft.EnglishIssueDate.Trim(),
            Duplicate = duplicateNo.Length == 0 ? "(Duplicate)(1-1)" : "(Duplicate)(" + duplicateNo + ")",
            TranslatedOn = DateTime.Now.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture),
        };
    }
}
