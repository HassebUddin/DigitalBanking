namespace DigitalBanking.Contracts;

public static class UserRoles
{
    public const string Customer = "Customer";
    public const string Admin = "Admin";
    public const string InternalEmployee = "InternalEmployee";
    public const string ExternalEmployee = "ExternalEmployee";

    public static bool IsStaff(string role) =>
        role is Admin or InternalEmployee or ExternalEmployee;

    public static bool IsEmployee(string role) =>
        role is InternalEmployee or ExternalEmployee;
}
