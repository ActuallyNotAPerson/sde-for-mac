namespace Sde.Calculation.Models;

public class CostAdjustment
{
	public int CostAdjustmentLookupID { get; set; }

	public decimal? Quantity { get; set; }

	public decimal? UnitCost { get; set; }

	public decimal? TotalCost { get; set; }
}
