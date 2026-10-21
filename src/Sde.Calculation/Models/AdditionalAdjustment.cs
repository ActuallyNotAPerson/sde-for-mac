namespace Sde.Calculation.Models;

public class AdditionalAdjustment
{
	public int zOrder { get; set; }

	public string AdjustmentName { get; set; }

	public decimal? Quantity { get; set; }

	public decimal? UnitCost { get; set; }

	public decimal? TotalCost { get; set; }
}
