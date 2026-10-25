using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts;

namespace RimGovernor.Brain.GameFacts.Defs;

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

/// <summary>RimWorld.GuestStatus, value for value.</summary>
[JsonConverter(typeof(WireEnumConverter<GuestStatus>))]
public enum GuestStatus
{
    [WireName("GUEST_STATUS_GUEST")]
    Guest = 0,
    [WireName("GUEST_STATUS_PRISONER")]
    Prisoner = 1,
    [WireName("GUEST_STATUS_SLAVE")]
    Slave = 2,
}

/// <summary>RimWorld.LearningCategory, value for value.</summary>
[JsonConverter(typeof(WireEnumConverter<LearningCategory>))]
public enum LearningCategory
{
    [WireName("LEARNING_CATEGORY_EMPTY")]
    Empty = 0,
    [WireName("LEARNING_CATEGORY_VERY_LOW")]
    VeryLow = 1,
    [WireName("LEARNING_CATEGORY_LOW")]
    Low = 2,
    [WireName("LEARNING_CATEGORY_SATISFIED")]
    Satisfied = 3,
    [WireName("LEARNING_CATEGORY_HIGH")]
    High = 4,
    [WireName("LEARNING_CATEGORY_EXTREME")]
    Extreme = 5,
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

/// <summary>RimWorld.TechLevel, value for value.</summary>
[JsonConverter(typeof(WireEnumConverter<TechLevel>))]
public enum TechLevel
{
    [WireName("TECH_LEVEL_UNDEFINED")]
    Undefined = 0,
    [WireName("TECH_LEVEL_ANIMAL")]
    Animal = 1,
    [WireName("TECH_LEVEL_NEOLITHIC")]
    Neolithic = 2,
    [WireName("TECH_LEVEL_MEDIEVAL")]
    Medieval = 3,
    [WireName("TECH_LEVEL_INDUSTRIAL")]
    Industrial = 4,
    [WireName("TECH_LEVEL_SPACER")]
    Spacer = 5,
    [WireName("TECH_LEVEL_ULTRA")]
    Ultra = 6,
    [WireName("TECH_LEVEL_ARCHOTECH")]
    Archotech = 7,
}
