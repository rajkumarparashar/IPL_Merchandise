using IPLMerchandise.Contracts.V1;
using IPLMerchandise.Web.Client.Pages.Admin;
using IPLMerchandise.Web.Tests.Fakes;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using MudBlazor;

namespace IPLMerchandise.Web.Tests.Pages;

public class AdminProductImageTests : StoreTestContext
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    public AdminProductImageTests() => SignInAs("Admin");

    [Fact]
    public void New_product_is_created_first_then_its_photo_is_uploaded_with_the_new_etag()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        var cut = RenderComponent<AdminProductNew>();
        cut.InvokeAsync(() => cut.FindComponent<MudTextField<string>>().Instance.ValueChanged.InvokeAsync("CSK Cap"));
        cut.InvokeAsync(() => cut.FindComponent<MudSelect<string>>().Instance.ValueChanged.InvokeAsync("CSK"));
        cut.InvokeAsync(() => cut.FindComponent<MudSelect<ProductType?>>().Instance.ValueChanged.InvokeAsync(ProductType.Cap));
        cut.InvokeAsync(() => cut.FindComponent<MudNumericField<decimal?>>().Instance.ValueChanged.InvokeAsync(499m));
        cut.InvokeAsync(() => cut.FindComponent<MudNumericField<int?>>().Instance.ValueChanged.InvokeAsync(10));

        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary(Png, "cap.png", contentType: "image/png"));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=image-preview]")));
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.EndsWith("/admin/products/csk-cap", nav.Uri));
        Assert.Equal(("csk-cap", "cap.png", "\"etag-1\""), Assert.Single(AdminApi.Uploads)); // ETag returned by the create
    }

    [Fact]
    public void Files_that_are_not_photos_are_refused_before_anything_is_sent()
    {
        var cut = RenderComponent<AdminProductNew>();

        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText("hello", "notes.txt", contentType: "text/plain"));

        cut.WaitForAssertion(() => Assert.Equal("Choose a JPEG, PNG or WebP image.", cut.Find("[data-testid=image-error]").TextContent));
        Assert.Empty(cut.FindAll("[data-testid=image-preview]"));
    }

    [Fact]
    public void A_new_photo_is_part_of_the_single_save_on_the_edit_page()
    {
        var cut = RenderEdit();

        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary(Png, "new.png", contentType: "image/png"));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=image-preview]")));
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Contains("Saved photo.", cut.Markup));
        Assert.Equal(("csk-cap", "new.png", "\"etag-1\""), Assert.Single(AdminApi.Uploads));
        Assert.Equal("https://images.test/csk-cap/new.png", cut.Find(".admin-preview [data-testid=product-photo]").GetAttribute("src"));
    }

    [Fact]
    public void Removing_the_photo_is_sent_only_on_save()
    {
        Catalog.Products[Catalog.Products.FindIndex(p => p.Id == "csk-cap")] = Samples.CskCap with { ImageUrl = "https://images.test/old.png" };
        var cut = RenderEdit();

        cut.Find("[data-testid=image-remove]").Click();
        Assert.Empty(AdminApi.Removals);
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Contains("Saved photo.", cut.Markup));
        Assert.Equal(("csk-cap", "\"etag-1\""), Assert.Single(AdminApi.Removals));
    }

    private IRenderedComponent<AdminProductEdit> RenderEdit()
    {
        var cut = RenderComponent<AdminProductEdit>(p => p.Add(x => x.Id, "csk-cap"));
        cut.WaitForState(() => cut.FindAll("[data-testid=image-picker]").Count == 1);
        return cut;
    }
}
