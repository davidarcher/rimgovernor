package policy

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func near(a, b float64) bool { return math.Abs(a-b) < 1e-9 }

func TestItemMarketValueQualityAndCondition(t *testing.T) {
	// Real flak vest: about 223 silver at Normal and full health.
	for _, c := range []struct {
		name      string
		base      float64
		quality   int
		condition float64
		want      float64
	}{
		{"awful halves", 223, 0, 1, 111.5},
		{"poor", 223, 1, 1, 167.25},
		{"normal", 223, 2, 1, 223},
		{"good", 223, 3, 1, 278.75},
		{"excellent", 223, 4, 1, 334.5},
		{"masterwork", 223, 5, 1, 557.5},
		{"legendary", 223, 6, 1, 1115},
		// Marine armor: a 4000 base would gain 1500 at Good, past the 500 cap.
		{"good gain capped", 4000, 3, 1, 4500},
		{"legendary gain capped", 4000, 6, 1, 7000},
		{"full health from 90 percent", 223, 2, .9, 223},
		{"above 90 percent flat", 223, 2, 1, 223},
		{"tattered at 60 percent", 200, 2, .6, 100},
		{"half health", 200, 2, .5, 20},
		{"midway 50 to 60", 200, 2, .55, 60},
		{"midway 60 to 90", 200, 2, .75, 150},
		{"destroyed", 200, 2, 0, 0},
		{"quality and condition compose", 200, 4, .6, 150},
	} {
		got, ok := ItemMarketValue(c.base, c.quality, c.condition).Value()
		if !ok || !near(got, c.want) {
			t.Errorf("%s: got %v %v, want %v", c.name, got, ok, c.want)
		}
	}
	for name, f := range map[string]domain.Fact[float64]{
		"quality low":  ItemMarketValue(100, -1, 1),
		"quality high": ItemMarketValue(100, 7, 1),
		"condition":    ItemMarketValue(100, 2, 1.2),
		"nan":          ItemMarketValue(100, 2, math.NaN()),
		"negative":     ItemMarketValue(-1, 2, 1),
	} {
		if _, ok := f.Value(); ok {
			t.Errorf("%s: want unknown", name)
		}
	}
}

func spendBed(id, room string, quality string, owners ...PawnID) SleepingBed {
	return SleepingBed{ID: id, Definition: "Bed", Humanlike: domain.Known(true), Medical: domain.Known(false), Prisoners: domain.Known(false),
		Room: domain.Known(room), Quality: domain.Known(quality), Stuff: domain.Known("WoodLog"), Owners: owners}
}

func spendRoom(id string, wealth float64) UpkeepRoom {
	return UpkeepRoom{ID: id, Quality: domain.Known(RoomQuality{Wealth: wealth})}
}

func spendInput(beds []SleepingBed, rooms []UpkeepRoom, pawns ...PersonalSpendPawn) PersonalSpendInput {
	return PersonalSpendInput{
		Sleeping: SleepingObservation{Beds: beds, Rooms: domain.Known(rooms)},
		BedPrice: func(def, stuff Resource) (float64, bool) {
			if def == "Bed" && stuff == "WoodLog" {
				return 100, true
			}
			if def == "DoubleBed" && stuff == "WoodLog" {
				return 200, true
			}
			return 0, false
		},
		Pawns: pawns,
	}
}

func spendPawn(id PawnID, gear ...PersonalItem) PersonalSpendPawn {
	return PersonalSpendPawn{ID: id, Free: true, Gear: domain.Known(gear)}
}

func wantSpent(t *testing.T, got map[PawnID]domain.Fact[float64], id PawnID, want float64) {
	t.Helper()
	v, ok := got[id].Value()
	if !ok || !near(v, want) {
		t.Errorf("%s spent = %v %v, want %v", id, v, ok, want)
	}
}

func wantUnknown(t *testing.T, got map[PawnID]domain.Fact[float64], id PawnID) {
	t.Helper()
	if v, ok := got[id].Value(); ok {
		t.Errorf("%s spent = %v, want unknown", id, v)
	}
}

func TestPersonalSpentSoloBedroomAndGear(t *testing.T) {
	vest := PersonalItem{Definition: "Apparel_FlakVest", Quality: 4, Condition: .6, Base: domain.Known(200.0)}
	club := PersonalItem{Definition: "MeleeWeapon_Club", Quality: 2, Condition: 1, Base: domain.Known(30.0)}
	// A good bed is 125; the room's 600 wealth includes it, so contents are 475.
	in := spendInput([]SleepingBed{spendBed("b1", "r1", "Good", "ann")}, []UpkeepRoom{spendRoom("r1", 600)}, spendPawn("ann", vest, club))
	got := PersonalSpent(in)
	wantSpent(t, got, "ann", 125+475+150+30)
}

func TestPersonalSpentSharedRoomSplitsEvenly(t *testing.T) {
	// A double bed (Normal, 200) of a couple in a 1000-wealth room: each pays
	// half the bed and half of the 800 contents.
	in := spendInput([]SleepingBed{spendBed("b1", "r1", "Normal", "ann", "bob")}, []UpkeepRoom{spendRoom("r1", 1000)}, spendPawn("ann"), spendPawn("bob"))
	in.Sleeping.Beds[0].Definition = "DoubleBed"
	got := PersonalSpent(in)
	wantSpent(t, got, "ann", 100+400)
	wantSpent(t, got, "bob", 100+400)
}

func TestPersonalSpentBarracksPaysOwnBedAndSplitsContents(t *testing.T) {
	beds := []SleepingBed{spendBed("b1", "r1", "Normal", "ann"), spendBed("b2", "r1", "Normal", "bob"), spendBed("b3", "r1", "Poor", "cy")}
	in := spendInput(beds, []UpkeepRoom{spendRoom("r1", 975)}, spendPawn("ann"), spendPawn("bob"), spendPawn("cy"))
	got := PersonalSpent(in)
	// Beds are 100 + 100 + 75; contents 700 split three ways.
	wantSpent(t, got, "ann", 100+700.0/3)
	wantSpent(t, got, "bob", 100+700.0/3)
	wantSpent(t, got, "cy", 75+700.0/3)
}

func TestPersonalSpentExclusions(t *testing.T) {
	slaveBed := spendBed("s1", "r2", "Normal", "ann")
	slaveBed.Slaves = true
	medical := spendBed("m1", "r3", "Normal", "ann")
	medical.Medical = domain.Known(true)
	prison := spendBed("p1", "r4", "Normal", "ann")
	prison.Prisoners = domain.Known(true)
	ownerless := spendBed("o1", "r5", "Normal")
	rooms := []UpkeepRoom{spendRoom("r2", 900), spendRoom("r3", 900), spendRoom("r4", 900), spendRoom("r5", 900)}
	slave := PersonalSpendPawn{ID: "sly", Gear: domain.Known([]PersonalItem{{Definition: "Apparel_Parka", Quality: 2, Condition: 1, Base: domain.Known(100.0)}})}
	got := PersonalSpent(spendInput([]SleepingBed{slaveBed, medical, prison, ownerless}, rooms, spendPawn("ann"), slave))
	wantSpent(t, got, "ann", 0)
	if _, ok := got["sly"]; ok {
		t.Error("a slave has no spent entry")
	}
}

func TestPersonalSpentUnknownInputsYieldUnknown(t *testing.T) {
	good := spendBed("b1", "r1", "Normal", "ann")
	rooms := []UpkeepRoom{spendRoom("r1", 500)}

	noRooms := spendInput([]SleepingBed{good}, nil, spendPawn("ann"), spendPawn("bob"))
	noRooms.Sleeping.Rooms = domain.Unknown[[]UpkeepRoom]()
	got := PersonalSpent(noRooms)
	wantUnknown(t, got, "ann")
	wantSpent(t, got, "bob", 0) // owns no bedroom, so the unread census is irrelevant

	unknownWealth := spendInput([]SleepingBed{good}, []UpkeepRoom{{ID: "r1"}}, spendPawn("ann"))
	wantUnknown(t, PersonalSpent(unknownWealth), "ann")

	noQuality := spendBed("b1", "r1", "Normal", "ann")
	noQuality.Quality = domain.Unknown[string]()
	wantUnknown(t, PersonalSpent(spendInput([]SleepingBed{noQuality}, rooms, spendPawn("ann"))), "ann")

	unpriced := spendBed("b1", "r1", "Normal", "ann")
	unpriced.Definition = "Mystery"
	wantUnknown(t, PersonalSpent(spendInput([]SleepingBed{unpriced}, rooms, spendPawn("ann"))), "ann")

	noGear := spendInput([]SleepingBed{good}, rooms, PersonalSpendPawn{ID: "ann", Free: true})
	wantUnknown(t, PersonalSpent(noGear), "ann")

	badItem := spendPawn("ann", PersonalItem{Definition: "Apparel_Parka", Quality: 9, Condition: 1, Base: domain.Known(100.0)})
	wantUnknown(t, PersonalSpent(spendInput([]SleepingBed{good}, rooms, badItem)), "ann")
	unpricedItem := spendPawn("ann", PersonalItem{Definition: "Apparel_Parka", Quality: 2, Condition: 1})
	wantUnknown(t, PersonalSpent(spendInput([]SleepingBed{good}, rooms, unpricedItem)), "ann")
}

func TestPersonalSpentRoomWealthBelowBedsFloorsAtZero(t *testing.T) {
	in := spendInput([]SleepingBed{spendBed("b1", "r1", "Normal", "ann")}, []UpkeepRoom{spendRoom("r1", 50)}, spendPawn("ann"))
	wantSpent(t, PersonalSpent(in), "ann", 100)
}

func TestPersonalItemOfPricesFromCost(t *testing.T) {
	it := PersonalItemOf(GearOption{Definition: "Apparel_Parka", Stuff: "Cloth", Quality: 3, Condition: 1, Cost: 80})
	v, _ := ItemMarketValue(func() float64 { b, _ := it.Base.Value(); return b }(), it.Quality, it.Condition).Value()
	if !near(v, 100) {
		t.Errorf("good parka = %v, want 100", v)
	}
}

func TestPartDiscountConstantsPinned(t *testing.T) {
	if PartRestoreDiscount != .25 || PartUpgradeDiscount != .75 || PartBionicTier != 1 {
		t.Fatalf("discount constants changed: %v %v %v", PartRestoreDiscount, PartUpgradeDiscount, PartBionicTier)
	}
}

func TestPersonalSpentInstalledPartsTierDiscount(t *testing.T) {
	prices := map[Resource]float64{"Peg": 100, "Prosthetic": 200, "Bionic": 400, "Archotech": 1000}
	part := func(hediff string, item Resource) InstalledPart {
		return InstalledPart{Hediff: hediff, Item: domain.Known(item), Tier: testPartTier(hediff)}
	}
	for _, c := range []struct {
		name  string
		parts []InstalledPart
		want  float64
	}{
		{"peg", []InstalledPart{part("PegLeg", "Peg")}, 25},
		{"prosthetic", []InstalledPart{part("SimpleProstheticArm", "Prosthetic")}, 50},
		{"bionic", []InstalledPart{part("BionicArm", "Bionic")}, 300},
		{"archotech", []InstalledPart{part("ArchotechEye", "Archotech")}, 750},
		{"several", []InstalledPart{part("PegLeg", "Peg"), part("BionicArm", "Bionic")}, 325},
		{"none", nil, 0},
	} {
		in := spendInput(nil, []UpkeepRoom{}, spendPawn("ann"))
		in.PartPrice = func(r Resource) (float64, bool) { v, ok := prices[r]; return v, ok }
		in.Pawns[0].Parts = c.parts
		t.Run(c.name, func(t *testing.T) { wantSpent(t, PersonalSpent(in), "ann", c.want) })
	}
}

func TestPersonalSpentInstalledPartsUnknownNeverZero(t *testing.T) {
	priced := func(Resource) (float64, bool) { return 100, true }
	for name, mutate := range map[string]func(*PersonalSpendInput){
		"no item":       func(in *PersonalSpendInput) { in.Pawns[0].Parts = []InstalledPart{{Hediff: "BionicArm", Tier: 1.25}} },
		"missing price": func(in *PersonalSpendInput) { in.PartPrice = func(Resource) (float64, bool) { return 0, false } },
		"no lookup":     func(in *PersonalSpendInput) { in.PartPrice = nil },
		"unread":        func(in *PersonalSpendInput) { in.Pawns[0].Parts = nil; in.Pawns[0].PartsUnread = true },
	} {
		in := spendInput(nil, []UpkeepRoom{}, spendPawn("ann"))
		in.PartPrice = priced
		in.Pawns[0].Parts = []InstalledPart{{Hediff: "BionicArm", Item: domain.Known(Resource("Bionic")), Tier: 1.25}}
		mutate(&in)
		t.Run(name, func(t *testing.T) { wantUnknown(t, PersonalSpent(in), "ann") })
	}
}
