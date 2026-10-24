using DigitalBanking.BuildingBlocks.Authentication;
using DigitalBanking.BuildingBlocks.Exceptions;
using DigitalBanking.BuildingBlocks.Web;
using DigitalBanking.BuildingBlocks.Security;
using DigitalBanking.Customer.Api.Application;
using DigitalBanking.Customer.Api.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DigitalBanking.Customer.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/customers")]
public sealed class CustomersController(CustomerService customerService, CurrentUser currentUser) : ControllerBase
{
    [HttpGet("get-or-create-customer-profile")]
    public async Task<ActionResult<CustomerResponse>> GetOrCreateCustomerProfile(CancellationToken cancellationToken)
    {
        return Ok(await customerService.GetOrCreateCustomerProfileAsync(currentUser.UserId, currentUser.Email, currentUser.FullName, cancellationToken));
    }

    // Called by the identity service right after registration, so the profile has the CNIC and phone the
    // customer typed even when the message bus is not running (the CustomerRegistered event remains as backup).
    [AllowAnonymous]
    [InternalApiKey]
    [HttpPost("internal/register")]
    public async Task<IActionResult> RegisterProfile(RegisteredCustomerRequest request, CancellationToken cancellationToken)
    {
        await customerService.CreateCustomerFromRegistrationAsync(request.UserId, request.Email, request.FullName, request.NationalId, request.PhoneNumber, request.Address, request.Region, cancellationToken);
        return NoContent();
    }

    // Registration asks this first: one CNIC may belong to only one customer in the whole bank.
    [AllowAnonymous]
    [InternalApiKey]
    [HttpGet("internal/national-id-taken")]
    public async Task<IActionResult> NationalIdTaken([FromQuery] string nationalId, CancellationToken cancellationToken)
    {
        return Ok(new { taken = await customerService.NationalIdTakenAsync(nationalId, cancellationToken) });
    }

    [HttpPut("update-customer")]
    public async Task<ActionResult<CustomerResponse>> UpdateCustomer(UpdateCustomerRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.FullName))
            throw new ValidationException("Full name is required.");

        return Ok(await customerService.UpdateCustomerAsync(currentUser.UserId, currentUser.Email, request, cancellationToken));
    }

    [Authorize(Roles = "Admin,InternalEmployee,ExternalEmployee")]
    [HttpGet("get-customers")]
    public async Task<ActionResult<IReadOnlyList<CustomerResponse>>> GetCustomers(CancellationToken cancellationToken)
    {
        return Ok(await customerService.GetCustomersAsync(cancellationToken));
    }

    // Staff list with paging: search (name, email, phone, CNIC) and KYC filter over every province.
    [Authorize(Roles = "Admin,InternalEmployee,ExternalEmployee")]
    [HttpGet("search-customers")]
    public async Task<ActionResult<PagedResult<CustomerResponse>>> SearchCustomers([FromQuery] string? search, [FromQuery] string? kyc, [FromQuery] int page, [FromQuery] int pageSize, CancellationToken cancellationToken)
    {
        return Ok(await customerService.SearchCustomersAsync(search, kyc, page, pageSize, cancellationToken));
    }

    [HttpGet("get-customer-by-id/{customerId:guid}")]
    public async Task<ActionResult<CustomerResponse>> GetCustomerById(Guid customerId, CancellationToken cancellationToken)
    {
        var customer = await customerService.GetCustomerByIdAsync(customerId, cancellationToken);
        if (!currentUser.CanViewBankRecords && customer.UserId != currentUser.UserId)
        {
            throw new ForbiddenException("You cannot view another customer's profile.");
        }

        return Ok(customer);
    }

    [Authorize(Roles = "Admin,InternalEmployee")]
    [HttpPost("update-kyc-status/{customerId:guid}")]
    public async Task<ActionResult<CustomerResponse>> UpdateKycStatus(Guid customerId, CancellationToken cancellationToken)
    {
        var status = "Verified";
        if (status is not ("Verified" or "Pending"))
        {
            throw new ValidationException("KYC status is invalid.");
        }

        return Ok(await customerService.UpdateKycStatusAsync(customerId, status, cancellationToken));
    }

    [Authorize(Roles = "Admin")]
    [HttpDelete("delete-customer/{customerId:guid}")]
    public async Task<IActionResult> DeleteCustomer(Guid customerId, CancellationToken cancellationToken)
    {
        await customerService.DeleteCustomerAsync(customerId, cancellationToken);
        return NoContent();
    }
}
