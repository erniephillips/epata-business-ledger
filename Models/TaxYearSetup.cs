using System.ComponentModel.DataAnnotations;

namespace EPATA.BusinessLedger.Models;

public sealed class TaxYearSetup : AuditableEntity
{
    public int TaxYear { get; set; } = DateTime.Today.Year;

    [MaxLength(180)]
    public string BusinessName { get; set; } = "EPATA 3D Prints";

    [MaxLength(180)]
    public string PrincipalBusinessActivity { get; set; } = "3D printed products and custom fabrication";

    [MaxLength(12)]
    public string PrincipalBusinessCode { get; set; } = string.Empty;

    [MaxLength(20)]
    public string? Ein { get; set; }

    [MaxLength(260)]
    public string BusinessAddress { get; set; } = string.Empty;

    [MaxLength(40)]
    public string AccountingMethod { get; set; } = "Cash";

    [MaxLength(40)]
    public string MateriallyParticipated { get; set; } = "Review";

    [MaxLength(40)]
    public string StartedOrAcquiredThisYear { get; set; } = "Review";

    [MaxLength(40)]
    public string MadeReportablePayments { get; set; } = "Review";

    [MaxLength(40)]
    public string FiledRequired1099s { get; set; } = "Not applicable / Review";

    [MaxLength(80)]
    public string AtRiskStatus { get; set; } = "Review if the business has a loss";

    [MaxLength(80)]
    public string CogsMethod { get; set; } = "Not confirmed";

    [MaxLength(80)]
    public string InventoryValuationMethod { get; set; } = "Cost";

    [MaxLength(40)]
    public string InventoryMethodChanged { get; set; } = "No / Review";

    public decimal BeginningInventory { get; set; }
    public decimal PurchasesLessPersonalUse { get; set; }
    public decimal CostOfLabor { get; set; }
    public decimal MaterialsAndSupplies { get; set; }
    public decimal OtherCogsCosts { get; set; }
    public decimal EndingInventory { get; set; }

    public decimal Form1099KTotal { get; set; }
    public decimal Form1099NecTotal { get; set; }
    public decimal Form1099MiscTotal { get; set; }
    public decimal OtherBusinessIncome { get; set; }

    [MaxLength(80)]
    public string VehicleDeductionMethod { get; set; } = "Not confirmed";

    public DateTime? VehiclePlacedInService { get; set; }
    public decimal CommutingMiles { get; set; }
    public decimal OtherPersonalMiles { get; set; }

    [MaxLength(40)]
    public string VehicleAvailableForPersonalUse { get; set; } = "Review";

    [MaxLength(40)]
    public string AnotherVehicleAvailable { get; set; } = "Review";

    [MaxLength(40)]
    public string VehicleEvidence { get; set; } = "Review";

    [MaxLength(40)]
    public string VehicleEvidenceWritten { get; set; } = "Review";

    [MaxLength(80)]
    public string HomeOfficeMethod { get; set; } = "Not claimed / Review";

    public decimal HomeSquareFeet { get; set; }
    public decimal OfficeSquareFeet { get; set; }
    public decimal HomeOfficeDeduction { get; set; }

    public string? Notes { get; set; }
}
