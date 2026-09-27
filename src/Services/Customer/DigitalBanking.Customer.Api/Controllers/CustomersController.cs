using DigitalBanking.BuildingBlocks.Authentication;
using DigitalBanking.BuildingBlocks.Exceptions;
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
