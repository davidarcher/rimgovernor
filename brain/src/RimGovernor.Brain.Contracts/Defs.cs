using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Contracts.Wire.Defs;

// The game enums the observation messages carry. The def mirror (defs.proto) itself is not a wire contract here.

[JsonConverter(typeof(WireEnumConverter<DevelopmentalStage>))]
public enum DevelopmentalStage
{
    [WireName("DEVELOPMENTAL_STAGE_NONE")]
    None = 0,
    [WireName("DEVELOPMENTAL_STAGE_NEWBORN")]
    Newborn = 1,
    [WireName("DEVELOPMENTAL_STAGE_BABY")]
    Baby = 2,
    [WireName("DEVELOPMENTAL_STAGE_CHILD")]
    Child = 4,
    [WireName("DEVELOPMENTAL_STAGE_ADULT")]
    Adult = 8,
}

[JsonConverter(typeof(WireEnumConverter<Gender>))]
public enum Gender
{
    [WireName("GENDER_NONE")]
    None = 0,
    [WireName("GENDER_MALE")]
    Male = 1,
    [WireName("GENDER_FEMALE")]
    Female = 2,
}

[JsonConverter(typeof(WireEnumConverter<HungerCategory>))]
public enum HungerCategory
{
    [WireName("HUNGER_CATEGORY_FED")]
    Fed = 0,
    [WireName("HUNGER_CATEGORY_HUNGRY")]
    Hungry = 1,
    [WireName("HUNGER_CATEGORY_URGENTLY_HUNGRY")]
    UrgentlyHungry = 2,
    [WireName("HUNGER_CATEGORY_STARVING")]
    Starving = 3,
}

[JsonConverter(typeof(WireEnumConverter<PriceType>))]
public enum PriceType
{
    [WireName("PRICE_TYPE_UNDEFINED")]
    Undefined = 0,
    [WireName("PRICE_TYPE_VERY_CHEAP")]
    VeryCheap = 1,
    [WireName("PRICE_TYPE_CHEAP")]
    Cheap = 2,
    [WireName("PRICE_TYPE_NORMAL")]
    Normal = 3,
    [WireName("PRICE_TYPE_EXPENSIVE")]
    Expensive = 4,
    [WireName("PRICE_TYPE_EXORBITANT")]
    Exorbitant = 5,
}

[JsonConverter(typeof(WireEnumConverter<QualityCategory>))]
public enum QualityCategory
{
    [WireName("QUALITY_CATEGORY_AWFUL")]
    Awful = 0,
    [WireName("QUALITY_CATEGORY_POOR")]
    Poor = 1,
    [WireName("QUALITY_CATEGORY_NORMAL")]
    Normal = 2,
    [WireName("QUALITY_CATEGORY_GOOD")]
    Good = 3,
    [WireName("QUALITY_CATEGORY_EXCELLENT")]
    Excellent = 4,
    [WireName("QUALITY_CATEGORY_MASTERWORK")]
    Masterwork = 5,
    [WireName("QUALITY_CATEGORY_LEGENDARY")]
    Legendary = 6,
}

[JsonConverter(typeof(WireEnumConverter<TradeCurrency>))]
public enum TradeCurrency
{
    [WireName("TRADE_CURRENCY_SILVER")]
    Silver = 0,
    [WireName("TRADE_CURRENCY_FAVOR")]
    Favor = 1,
}

