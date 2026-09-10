"""Player-facing deterministic settings; edits share ColonyPlan and direction guards."""
from dataclasses import asdict
from typing import Literal
from pydantic import BaseModel, ConfigDict, Field, model_validator
from .colony_policy import ColonyPolicy
from .strategic_state import fingerprint


class PolicyChanges(BaseModel):
    model_config=ConfigDict(extra='forbid',allow_inf_nan=False)
    execution_speed: Literal['Normal','Fast','Superfast'] | None=None
    food_min_days: float | None=Field(default=None,gt=0,le=120,strict=True)
    food_target_days: float | None=Field(default=None,ge=1,le=120,strict=True)
    wood_min: int | None=Field(default=None,ge=0,le=10000,strict=True)
    wood_target: int | None=Field(default=None,ge=1,le=10000,strict=True)
    wood_max: int | None=Field(default=None,ge=1,le=10000,strict=True)
    wood_reserve: int | None=Field(default=None,ge=0,le=10000,strict=True)
    medicine_min_per_colonist: int | None=Field(default=None,ge=0,le=19,strict=True)
    medicine_target_per_colonist: int | None=Field(default=None,ge=1,le=20,strict=True)
    animal_feed_min_days: float | None=Field(default=None,gt=0,le=30,strict=True)
    animal_feed_target_days: float | None=Field(default=None,gt=0,le=30,strict=True)
    max_development_projects: int | None=Field(default=None,ge=1,le=8,strict=True)
    temperature_enter_low: float | None=Field(default=None,ge=-10,le=40,strict=True)
    temperature_exit_low: float | None=Field(default=None,ge=-10,le=40,strict=True)
    temperature_exit_high: float | None=Field(default=None,ge=-10,le=40,strict=True)
    temperature_enter_high: float | None=Field(default=None,ge=-10,le=40,strict=True)

    @model_validator(mode='after')
    def supplied_values(self):
        if not self.model_fields_set or any(getattr(self,k) is None for k in self.model_fields_set):
            raise ValueError('Provide at least one setting with a value')
        return self


class PolicyUpdate(BaseModel):
    model_config=ConfigDict(extra='forbid')
    session_id: str=Field(min_length=1,max_length=300)
    expected_version: str=Field(pattern=r'^[a-f0-9]{64}$')
    changes: PolicyChanges


FIELDS=[
    ('animal_feed_min_days','Animals','Replenish below','days','Begin feed acquisition below this observed reachable reserve.'),
    ('animal_feed_target_days','Animals','Feed target','days','Continue until reachable feed covers this native demand, including competing eaters and rot deadlines.'),
    ('medicine_min_per_colonist','Medicine','Replenish below','per colonist','Start replenishing usable reachable medicine below this reserve. Existing care policies stay in effect.'),
    ('medicine_target_per_colonist','Medicine','Medicine target','per colonist','Continue ordinary acquisition until this reserve is observed. Future harvest is not stock.'),
    ('max_development_projects','Development','Concurrent projects','projects','Limit new optional projects to this count and the observed available workers. Accepted work is retained when capacity falls; player work remains explicit.'),
    ('execution_speed','Operation','Game speed','', 'Normal, Fast or Superfast during autonomous work. Reviews and safety holds still pause the game.'),
    ('food_min_days','Food','Replenish below','days','Start acquiring food below this stock runway.'),
    ('food_target_days','Food','Food target','days','Keep the food goal active until this stock runway is reached. Future harvest is not counted as stock.'),
    ('wood_min','Wood','Replenish below','wood','Begin acquiring wood below this amount.'),
    ('wood_target','Wood','Wood target','wood','Stop routine acquisition when stock reaches this target.'),
    ('wood_max','Wood','Wood ceiling','wood','Upper bound for the configured target; native deliveries can overshoot.'),
    ('wood_reserve','Wood','Development reserve','wood','Keep this amount available when planning optional development. Essential work can use it.'),
    ('temperature_enter_low','Temperature','Heat below','°C','Start warming sleeping rooms below this temperature.'),
    ('temperature_exit_low','Temperature','Heat until','°C','Stop requesting more heating after this recovery temperature.'),
    ('temperature_exit_high','Temperature','Cool until','°C','Stop requesting more cooling after this recovery temperature.'),
    ('temperature_enter_high','Temperature','Cool above','°C','Start cooling sleeping rooms above this temperature.'),
    ('foothold_food_days','Verification','Minimum foothold food','days','Food required to certify the starter colony. This verification rule is read-only here.'),
    ('max_method_attempts','Verification','Method attempt limit','attempts','Maximum layout alternatives per search and confirmed treatment replacements per action.'),
    ('blocked_after_ticks','Verification','No-progress limit','game ticks','Block ordinary work after this many game ticks without measurable progress; 60,000 ticks is one day.'),
]


def effective_policy(plan):
    return asdict(ColonyPolicy(**dict(asdict(ColonyPolicy()),**plan.control.get('policy',{}))))


def settings_state(plan):
    values=effective_policy(plan)
    return dict(values=values,version=fingerprint(values),defaults=asdict(ColonyPolicy()),
        source=plan.control.get('policy_source','Defaults / player chat'),
        fields=[dict(key=k,group=g,label=label,unit=unit,help=help_,editable=k in PolicyChanges.model_fields)
                for k,g,label,unit,help_ in FIELDS])


async def update_policy(rt, request):
    async with rt.lock:
        if not rt.connected: raise ValueError('Wait for the colony connection')
        await rt.sync_identity()
        if request.session_id!=rt.context_token: raise ValueError('The loaded colony changed; review its settings before saving')
        previous=effective_policy(rt.current_plan)
        if request.expected_version!=fingerprint(previous):
            raise ValueError('Settings changed in another view or in chat; reload the current values before saving')
        changes=request.changes.model_dump(exclude_unset=True)
        policy=ColonyPolicy(**dict(previous,**changes))
        if not policy.temperature_enter_low<policy.temperature_exit_low<=policy.temperature_exit_high<policy.temperature_enter_high:
            raise ValueError('Temperatures must follow: heat below < heat until <= cool until < cool above')
        if policy.wood_reserve>policy.wood_max: raise ValueError('Wood reserve must not exceed the wood ceiling')
        changed={k:v for k,v in changes.items() if previous[k]!=v}
        if not changed: return settings_state(rt.current_plan)
        rt.current_plan.control.setdefault('policy',{}).update(changed)
        rt.current_plan.control['policy_source']='PLAYER'
        rt.current_plan.control['latches']={}
        goal=rt.current_plan.colony_goals.get('EnsureFoodSupply')
        if goal and 'food_target_days' in changed:
            goal.target['food_days']=policy.food_target_days
            goal.source='PLAYER'
        rt.note('controller_policy_changed','Player updated autopilot settings',source='PLAYER',changes=changed)
        # This invalidates in-flight interpretations/orders without invoking the LLM.
        await rt.steer('Autopilot settings updated.',interpret=False)
        return settings_state(rt.current_plan)
