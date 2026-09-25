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
    [HttpGet("me")]
    public async Task<ActionResult<CustomerResponse>> GetMine(CancellationToken cancellationToken)
    {
        return Ok(await customerService.GetOrCreateMineAsync(currentUser.UserId, currentUser.Email, currentUser.FullName, cancellationToken));
    }

    [HttpPut("me")]
    public async Task<ActionResult<CustomerResponse>> UpdateMine(UpdateCustomerRequest request, CancellationToken cancellationToken)
    {
        return Ok(await customerService.UpdateAsync(currentUser.UserId, currentUser.Email, request, cancellationToken));
    }

    [Authorize(Roles = "Admin")]
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<CustomerResponse>>> List(CancellationToken cancellationToken)
    {
        return Ok(await customerService.ListAsync(cancellationToken));
    }

    [HttpGet("{customerId:guid}")]
    public async Task<ActionResult<CustomerResponse>> GetById(Guid customerId, CancellationToken cancellationToken)
    {
        var customer = await customerService.GetByIdAsync(customerId, cancellationToken);
        if (!currentUser.IsAdmin && customer.UserId != currentUser.UserId)
        {
            throw new ForbiddenException("You cannot view another customer's profile.");
        }

        return Ok(customer);
    }

    [Authorize(Roles = "Admin")]
    [HttpDelete("{customerId:guid}")]
    public async Task<IActionResult> Delete(Guid customerId, CancellationToken cancellationToken)
    {
        await customerService.DeleteAsync(customerId, cancellationToken);
        return NoContent();
    }
}
