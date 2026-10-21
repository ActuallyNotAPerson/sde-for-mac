# Hand-entry worksheet

Generated from `cases/*.json` by `CrossCheckKitTests`; don't edit it by hand. Enter each case in FEMA's SDE 3.0 (Windows) with the values below, tab by tab, then compare the **Output Summary** with the expected results. Fields not listed can be left blank. See README.md.

## 01-ranch-flood: 123 River Street

**Add New Residential Assessment.**

| Field | Value |
|---|---|
| **Address tab** | |
| Owner first / last name | Pat Example |
| Street number / name / suffix | 123 River Street |
| City / State / Zip | Exampleville, Ohio, 43702 |
| **Structure/Damage/NFIP tab** | |
| NFIP Community ID | 399990 |
| Story | One Story (Standard) |
| Residence Type | Single Family Residence |
| Foundation | Crawlspace |
| Superstructure | Stud-framed (Standard) |
| Roof Covering | Shingles - Asphalt, Wood (Standard) |
| Exterior Finish | Siding or Stucco (Standard) |
| HVAC System | Heating and/or Cooling |
| Year of Construction | 1962 |
| Quality | Good |
| Inspector Name | Inspector A |
| Date Damage Occurred | 9/14/2026 |
| Cause of Damage | Flood |
| **Cost tab** | |
| Base Cost | 118.75 |
| Geographic Adjustment | 0.93 |
| Total Square Footage (enter directly) | 1,450 |
| Cost adjustment: Garage | 240 × $32.50 |
| Additional adjustment: Shed | 1 × $2,500.00 |
| Depreciation Rating | 4 - Average Condition |
| **Element Percentages tab** (% damaged) | |
| Foundation | 10 % |
| Superstructure | 20 % |
| Exterior Finish | 35 % |
| Interior Finish | 90 % |
| Doors and Windows | 60 % |
| Cabinets and Countertops | 100 % |
| Floor Finish | 100 % |
| Plumbing | 50 % |
| Electrical | 70 % |
| Appliances | 100 % |
| HVAC | 100 % |
| **Output Summary tab** | |
| Basis for value of Structure | Computed Actual Cash Value |
| Basis for cost of Repairs/Improvements | Computed Damages |

**Expected:**

| Output | Value |
|---|---|
| Replacement Cost (Cost tab) | 170,434.38 |
| Depreciation Percentage | 24.2 |
| Computed Actual Cash Value | 129,189.26 |
| Computed Damages | 96,900.46 |
| Percent Damaged | 75.0 % |
| Determination | Substantially Damaged |

## 02-twostory-close: 48 Mill Creek Street

**Add New Residential Assessment.**

| Field | Value |
|---|---|
| **Address tab** | |
| Owner first / last name | Dana Rivera |
| Street number / name / suffix | 48 Mill Creek Street |
| City / State / Zip | Exampleville, Ohio, 43715 |
| **Structure/Damage/NFIP tab** | |
| NFIP Community ID | 399990 |
| Story | Two or More Stories |
| Residence Type | Single Family Residence |
| Foundation | Basement |
| Superstructure | Stud-framed (Standard) |
| Roof Covering | Shingles - Asphalt, Wood (Standard) |
| Exterior Finish | Brick Veneer |
| HVAC System | Heating and/or Cooling |
| Year of Construction | 1928 |
| Quality | Average |
| Inspector Name | Inspector A |
| Date Damage Occurred | 9/14/2026 |
| Cause of Damage | Flood |
| **Cost tab** | |
| Base Cost | 132.4 |
| Geographic Adjustment | 0.93 |
| Total Square Footage (enter directly) | 2,200 |
| Depreciation Rating | 3 - Requires Some Repairs |
| **Element Percentages tab** (% damaged) | |
| Foundation | 30 % |
| Interior Finish | 60 % |
| Doors and Windows | 30 % |
| Floor Finish | 80 % |
| Plumbing | 40 % |
| Electrical | 50 % |
| Appliances | 60 % |
| HVAC | 100 % |
| **Output Summary tab** | |
| Basis for value of Structure | Computed Actual Cash Value |
| Basis for cost of Repairs/Improvements | Computed Damages |

**Expected:**

| Output | Value |
|---|---|
| Replacement Cost (Cost tab) | 270,890.4 |
| Depreciation Percentage | 38.8 |
| Computed Actual Cash Value | 165,784.92 |
| Computed Damages | 82,675.75 |
| Percent Damaged | 49.9 % |
| Determination | Not Substantially Damaged |

## 03-office-minor: 700 Riverside Street

**Add New Non-Residential Assessment.**

| Field | Value |
|---|---|
| **Address tab** | |
| Owner first / last name | Queen City Properties LLC |
| Street number / name / suffix | 700 Riverside Street |
| City / State / Zip | Exampleville, Ohio, 43704 |
| **Structure/Damage/NFIP tab** | |
| NFIP Community ID | 399990 |
| Story | 1 Story |
| Structure Use | Office Building |
| Sprinkler System | Yes |
| Year of Construction | 1985 |
| Inspector Name | Inspector A |
| Date Damage Occurred | 9/14/2026 |
| Cause of Damage | Flood |
| **Cost tab** | |
| Base Cost | 185 |
| Geographic Adjustment | 0.93 |
| Total Square Footage (enter directly) | 6,400 |
| Depreciation Rating | 4 - Average Condition |
| **Element Percentages tab** (% damaged) | |
| Plumbing | 10 % |
| Electrical | 20 % |
| HVAC | 40 % |
| Interiors | 35 % |
| **Output Summary tab** | |
| Basis for value of Structure | Computed Actual Cash Value |
| Basis for cost of Repairs/Improvements | Computed Damages |

**Expected:**

| Output | Value |
|---|---|
| Replacement Cost (Cost tab) | 1,101,120 |
| Depreciation Percentage | 21.1 |
| Computed Actual Cash Value | 868,783.68 |
| Computed Damages | 179,097.17 |
| Percent Damaged | 20.6 % |
| Determination | Not Substantially Damaged |

## 04-townhouse-tax-value: 22 Front Street

**Add New Residential Assessment.**

| Field | Value |
|---|---|
| **Address tab** | |
| Owner first / last name | Avery Lin |
| Street number / name / suffix | 22 Front Street |
| City / State / Zip | Exampleville, Ohio, 43702 |
| **Structure/Damage/NFIP tab** | |
| NFIP Community ID | 399990 |
| Story | Two or More Stories |
| Residence Type | Town or Row House |
| Foundation | Crawlspace |
| Superstructure | Masonry |
| Roof Covering | Standing Seam (Metal) |
| Exterior Finish | Brick Veneer |
| HVAC System | Heating and/or Cooling |
| Year of Construction | 1924 |
| Quality | Average |
| Inspector Name | Inspector A |
| Date Damage Occurred | 9/14/2026 |
| Cause of Damage | Flood |
| **Cost tab** | |
| Base Cost | 142.3 |
| Geographic Adjustment | 0.97 |
| Total Square Footage (enter directly) | 1,680 |
| Cost adjustment: Fireplaces | 1 × $4,200.00 |
| Depreciation Rating | 3 - Requires Some Repairs |
| **Element Percentages tab** (% damaged) | |
| Foundation | 15 % |
| Interior Finish | 70 % |
| Doors and Windows | 30 % |
| Cabinets and Countertops | 100 % |
| Floor Finish | 100 % |
| Plumbing | 40 % |
| Electrical | 60 % |
| Appliances | 100 % |
| HVAC | 100 % |
| **Output Summary tab** | |
| Tax Assessed Value | 118,400 |
| Tax Factor Adjustment | 1.25 |
| Basis for value of Structure | Adj. Tax Assessed Value |
| Basis for cost of Repairs/Improvements | Computed Damages |

**Expected:**

| Output | Value |
|---|---|
| Replacement Cost (Cost tab) | 236,092.08 |
| Depreciation Percentage | 38.8 |
| Computed Actual Cash Value | 144,488.35 |
| Computed Damages | 83,458.55 |
| Percent Damaged | 56.4 % |
| Determination | Substantially Damaged |

## 05-appraisal-contractor: 315 Mill Street

**Add New Residential Assessment.**

| Field | Value |
|---|---|
| **Address tab** | |
| Owner first / last name | Sam Ortiz |
| Street number / name / suffix | 315 Mill Street |
| City / State / Zip | Exampleville, Ohio, 43704 |
| **Structure/Damage/NFIP tab** | |
| NFIP Community ID | 399990 |
| Story | One Story (Standard) |
| Residence Type | Single Family Residence |
| Foundation | Basement |
| Superstructure | Stud-framed (Standard) |
| Roof Covering | Shingles - Asphalt, Wood (Standard) |
| Exterior Finish | Siding or Stucco (Standard) |
| HVAC System | Heating and/or Cooling |
| Year of Construction | 1958 |
| Quality | Good |
| Inspector Name | Inspector A |
| Date Damage Occurred | 9/14/2026 |
| Cause of Damage | Flood |
| **Cost tab** | |
| Base Cost | 1 |
| Geographic Adjustment | 1 |
| Total Square Footage (enter directly) | 1,900 |
| Depreciation Rating | Other |
| Depreciation Percentage | 1 |
| Depreciation explanation | Used Professional Market Appraisal value prepared by the ABC Appraisal Company on 09/30/26 instead of depreciation |
| **Element Percentages tab** (% damaged) | |
| **Output Summary tab** | |
| Professional Market Appraisal | 210,000 |
| Contractor Estimate | 112,500 |
| Basis for value of Structure | Professional Market Appraisal |
| Basis for cost of Repairs/Improvements | Contractor Estimate |

**Expected:**

| Output | Value |
|---|---|
| Replacement Cost (Cost tab) | 1,900 |
| Depreciation Percentage | 1 |
| Computed Actual Cash Value | 1,881 |
| Computed Damages | 0 |
| Percent Damaged | 53.6 % |
| Determination | Substantially Damaged |

## 06-manufactured-home: 8 Willow Street

**Add New Residential Assessment.**

| Field | Value |
|---|---|
| **Address tab** | |
| Owner first / last name | Jo Baker |
| Street number / name / suffix | 8 Willow Street |
| City / State / Zip | Exampleville, Ohio, 43733 |
| **Structure/Damage/NFIP tab** | |
| NFIP Community ID | 399990 |
| Story | One Story (Standard) |
| Residence Type | Manufactured House |
| Foundation | Piers and Posts |
| Superstructure | Stud-framed (Standard) |
| Roof Covering | Standing Seam (Metal) |
| Exterior Finish | Siding or Stucco (Standard) |
| HVAC System | Heating and/or Cooling |
| Year of Construction | 1994 |
| Quality | Budget |
| Inspector Name | Inspector A |
| Date Damage Occurred | 9/14/2026 |
| Cause of Damage | Flood and Wind |
| **Cost tab** | |
| Base Cost | 68.5 |
| Geographic Adjustment | 0.93 |
| Total Square Footage (enter directly) | 1,216 |
| Cost adjustment: Expando | 120 × $45.00 |
| Cost adjustment: Skirting | 152 × $9.50 |
| Depreciation Rating | 2 - Requires Extensive Repairs |
| **Element Percentages tab** (% damaged) | |
| Superstructure | 5 % |
| Interior Finish | 20 % |
| Doors and Windows | 10 % |
| Floor Finish | 40 % |
| Plumbing | 10 % |
| Electrical | 15 % |
| Appliances | 25 % |
| HVAC | 50 % |
| Skirting / Forms Piers | 100 % |
| **Output Summary tab** | |
| Basis for value of Structure | Computed Actual Cash Value |
| Basis for cost of Repairs/Improvements | Computed Damages |

**Expected:**

| Output | Value |
|---|---|
| Replacement Cost (Cost tab) | 84,309.28 |
| Depreciation Percentage | 66.5 |
| Computed Actual Cash Value | 28,243.61 |
| Computed Damages | 16,145.23 |
| Percent Damaged | 57.2 % |
| Determination | Substantially Damaged |

## 07-no-physical-damage: 41 Hill Street

**Add New Residential Assessment.**

| Field | Value |
|---|---|
| **Address tab** | |
| Street number / name / suffix | 41 Hill Street |
| City / State / Zip | Exampleville, Ohio, 43702 |
| **Structure/Damage/NFIP tab** | |
| NFIP Community ID | 399990 |
| Story | One Story (Standard) |
| Residence Type | Single Family Residence |
| Inspector Name | Inspector A |
| Date Damage Occurred | 9/14/2026 |
| Cause of Damage | Flood |
| Damage Undetermined | Yes: No Physical Damage Sustained |

**Expected:**

| Output | Value |
|---|---|
| Percent Damaged | 0.0 % |
| Determination | No Physical Damage Sustained |

## 08-hotel-community-estimate: 1 Riverfront Street

**Add New Non-Residential Assessment.**

| Field | Value |
|---|---|
| **Address tab** | |
| Owner first / last name | Riverfront Hospitality Inc. |
| Street number / name / suffix | 1 Riverfront Street |
| City / State / Zip | Exampleville, Ohio, 43702 |
| **Structure/Damage/NFIP tab** | |
| NFIP Community ID | 399990 |
| Story | 5 or more |
| Structure Use | Hotel |
| Sprinkler System | Yes |
| Conveyance | Yes |
| Year of Construction | 2001 |
| Quality | Good |
| Inspector Name | Inspector A |
| Date Damage Occurred | 9/14/2026 |
| Cause of Damage | Flood |
| **Cost tab** | |
| Base Cost | 248.75 |
| Geographic Adjustment | 1.02 |
| Total Square Footage (enter directly) | 86,500 |
| Depreciation Rating | 5 - Above Average Condition |
| **Element Percentages tab** (% damaged) | |
| Plumbing | 10 % |
| Electrical | 25 % |
| HVAC | 40 % |
| Interiors | 30 % |
| **Output Summary tab** | |
| Community Estimate | 3,250,000 |
| Basis for value of Structure | Computed Actual Cash Value |
| Basis for cost of Repairs/Improvements | Community Estimate |

**Expected:**

| Output | Value |
|---|---|
| Replacement Cost (Cost tab) | 21,947,212.5 |
| Depreciation Percentage | 13.2 |
| Computed Actual Cash Value | 19,050,180.45 |
| Computed Damages | 4,027,313.49 |
| Percent Damaged | 17.1 % |
| Determination | Not Substantially Damaged |

## 09-very-poor-capped: 6 Ferry Street

**Add New Residential Assessment.**

| Field | Value |
|---|---|
| **Address tab** | |
| Owner first / last name | Lee Grant |
| Street number / name / suffix | 6 Ferry Street |
| City / State / Zip | Exampleville, Ohio, 43726 |
| **Structure/Damage/NFIP tab** | |
| NFIP Community ID | 399990 |
| Story | One Story (Standard) |
| Residence Type | Single Family Residence |
| Foundation | Piles |
| Superstructure | Stud-framed (Standard) |
| Roof Covering | Shingles - Asphalt, Wood (Standard) |
| Exterior Finish | Siding or Stucco (Standard) |
| HVAC System | None |
| Year of Construction | 1931 |
| Quality | Low |
| Inspector Name | Inspector A |
| Date Damage Occurred | 9/14/2026 |
| Cause of Damage | Flood and Wind |
| **Cost tab** | |
| Base Cost | 96 |
| Geographic Adjustment | 0.93 |
| Total Square Footage (enter directly) | 860 |
| Depreciation Rating | 1 - Very Poor Condition |
| **Element Percentages tab** (% damaged) | |
| Foundation | 100 % |
| Superstructure | 100 % |
| Roof Covering | 100 % |
| Exterior Finish | 100 % |
| Interior Finish | 100 % |
| Doors and Windows | 100 % |
| Cabinets and Countertops | 100 % |
| Floor Finish | 100 % |
| Plumbing | 100 % |
| Electrical | 100 % |
| Appliances | 100 % |
| **Output Summary tab** | |
| Basis for value of Structure | Computed Actual Cash Value |
| Basis for cost of Repairs/Improvements | Computed Damages |

**Expected:**

| Output | Value |
|---|---|
| Replacement Cost (Cost tab) | 76,780.8 |
| Depreciation Percentage | 88.9 |
| Computed Actual Cash Value | 8,522.67 |
| Computed Damages | 76,780.8 |
| Percent Damaged | 100.0 % |
| Determination | Substantially Damaged |
