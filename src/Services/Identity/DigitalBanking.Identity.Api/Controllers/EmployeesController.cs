using DigitalBanking.BuildingBlocks.Exceptions;
using DigitalBanking.Contracts;
using DigitalBanking.Identity.Api.Application;
using DigitalBanking.Identity.Api.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DigitalBanking.Identity.Api.Controllers;

[ApiController]
[Route("api/employees")]
public sealed class EmployeesController(EmployeeService employeeService) : ControllerBase
{
    [Authorize(Roles = "Admin")]
    [HttpPost("create-employee")]
    public async Task<ActionResult<DirectoryUserResponse>> CreateEmployee(CreateEmployeeRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.FullName) || string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 8 || request.Role is not (UserRoles.InternalEmployee or UserRoles.ExternalEmployee))
        {
            throw new ValidationException("Email, full name, a password of at least 8 characters, and an employee role are required.");
        }

        return Ok(await employeeService.CreateEmployeeAsync(request, cancellationToken));
    }
}
