using WebDS_Test.Domain;
using Xunit;

namespace WebDS_Test.Tests;

public class PostSaleRulesTests
{
    [Fact]
    public void Cancellation_BeforePackaging_AllRefunded()
    {
        var a = PostSaleRules.GetCancellationAllocation(beforePackaging: true, afterTrackingNumber: false);
        Assert.Equal(FeeBearer.Seller, a.ItemPriceBearer);
        Assert.Equal(FeeBearer.Seller, a.ShippingFeeBearer);
        Assert.Equal(FeeBearer.Seller, a.CreditCardFeeBearer);
    }

    [Fact]
    public void Cancellation_AfterPacking_NoCardFee()
    {
        var a = PostSaleRules.GetCancellationAllocation(beforePackaging: false, afterTrackingNumber: false);
        Assert.Equal(FeeBearer.Seller, a.ItemPriceBearer);
        Assert.Equal(FeeBearer.Seller, a.ShippingFeeBearer);
        Assert.Equal(FeeBearer.NotApplicable, a.CreditCardFeeBearer);
    }

    [Fact]
    public void Cancellation_AfterTrackingNumber_Declined_NoRefund()
    {
        var a = PostSaleRules.GetCancellationAllocation(beforePackaging: true, afterTrackingNumber: true);
        Assert.Equal(FeeBearer.Customer, a.ItemPriceBearer);
        Assert.Equal(FeeBearer.Customer, a.ShippingFeeBearer);
    }

    [Fact]
    public void Return_ChangedMind_CustomerPays()
    {
        var a = PostSaleRules.GetReturnAllocation(PostSaleReason.ChangedMind, isOrderLevel: true);
        Assert.Equal(FeeBearer.Customer, a.ItemPriceBearer);
        Assert.Equal(FeeBearer.Customer, a.PickupFeeBearer);
    }

    [Fact]
    public void Return_WrongItem_SellerPays()
    {
        var a = PostSaleRules.GetReturnAllocation(PostSaleReason.WrongItem, isOrderLevel: false);
        Assert.Equal(FeeBearer.Seller, a.ItemPriceBearer);
        Assert.Equal(FeeBearer.Seller, a.PickupFeeBearer);
    }

    [Fact]
    public void Rts_CustomerBearsShipping()
    {
        var a = PostSaleRules.GetRtsAllocation();
        Assert.Equal(FeeBearer.Customer, a.ShippingFeeBearer);
        Assert.Equal(FeeBearer.NotApplicable, a.PickupFeeBearer);
    }

    [Fact]
    public void Warranty_WithinGrace_SellerPays()
    {
        var a = PostSaleRules.GetWarrantyAllocation(withinGracePeriod: true, canBeRepaired: true);
        Assert.Equal(FeeBearer.Seller, a.ItemPriceBearer);
    }

    [Fact]
    public void Warranty_Repairable_SellerPaysWork()
    {
        var a = PostSaleRules.GetWarrantyAllocation(withinGracePeriod: false, canBeRepaired: true);
        Assert.Equal(FeeBearer.NotApplicable, a.ItemPriceBearer);
        Assert.Equal(FeeBearer.Seller, a.PickupFeeBearer);
    }
}
