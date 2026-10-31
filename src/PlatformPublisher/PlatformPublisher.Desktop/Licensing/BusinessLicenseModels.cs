namespace PlatformPublisher.Desktop.Licensing;

public sealed class BusinessLicenseDraft
{
    public string SourcePath { get; set; } = "";
    public string Kind { get; set; } = "公司";
    public string CreditCode { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string PersonRole { get; set; } = "法定代表人";
    public string PersonName { get; set; } = "";
    public string ExtraLabel { get; set; } = "注册资本";
    public string ExtraValue { get; set; } = "";
    public string DateLabel { get; set; } = "成立日期";
    public string DateValue { get; set; } = "";
    public string AddressLabel { get; set; } = "住所";
    public string Address { get; set; } = "";
    public string Scope { get; set; } = "";
    public string Authority { get; set; } = "";
    public string IssueDate { get; set; } = "";
    public string DuplicateNo { get; set; } = "";

    public string EnglishName { get; set; } = "";
    public string EnglishType { get; set; } = "";
    public string EnglishPersonRole { get; set; } = "Legal Representative";
    public string EnglishPersonName { get; set; } = "";
    public string EnglishExtraLabel { get; set; } = "Registered Capital";
    public string EnglishExtraValue { get; set; } = "";
    public string EnglishDateLabel { get; set; } = "Date of Establishment";
    public string EnglishDateValue { get; set; } = "";
    public string EnglishAddressLabel { get; set; } = "Domicile";
    public string EnglishAddress { get; set; } = "";
    public string EnglishScope { get; set; } = "";
    public string EnglishAuthority { get; set; } = "";
    public string EnglishIssueDate { get; set; } = "";
    public string EnglishDuplicateNo { get; set; } = "";

    public string DisplayName => string.IsNullOrWhiteSpace(PersonName)
        ? Path.GetFileName(SourcePath)
        : $"{PersonName} · {Path.GetFileName(SourcePath)}";
}
