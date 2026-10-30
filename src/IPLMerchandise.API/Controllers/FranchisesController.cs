using Asp.Versioning;
using IPLMerchandise.Application.Catalog;
using IPLMerchandise.Contracts.V1;
using Microsoft.AspNetCore.Mvc;

namespace IPLMerchandise.API.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/franchises")]
public sealed class FranchisesController : ControllerBase
{
    [HttpGet]
    [ResponseCache(Duration = 3600)]
    public ActionResult<IReadOnlyList<FranchiseDto>> List() => Ok(CatalogService.GetFranchises());
}
