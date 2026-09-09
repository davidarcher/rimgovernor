import importlib.util
import json
from pathlib import Path
import pytest

spec=importlib.util.spec_from_file_location('semantic_benchmark',Path(__file__).parents[1]/'scripts/semantic_command_benchmark.py')
benchmark=importlib.util.module_from_spec(spec)
spec.loader.exec_module(benchmark)


def call(kind,**arguments):
    return {'function':{'name':kind,'arguments':json.dumps(arguments)}}


POLICY={'kind':'ModifyResourcePolicy','resource':'ComponentIndustrial','spending':'defense_only'}


@pytest.mark.parametrize('arguments',[
    {'resource':'Steel','spending':'defense_only'},
    {'resource':'ComponentIndustrial','spending':'stop'},
])
def test_valid_schema_wrong_policy_is_semantic_failure(arguments):
    result=benchmark.score({'tool_calls':[call('ModifyResourcePolicy',**arguments)]},POLICY)
    assert not result['correct'] and result['semantic_errors']==1 and result['schema_failures']==0


def test_reserve_on_spending_command_is_a_schema_failure():
    result=benchmark.score({'tool_calls':[call('ModifyResourcePolicy',resource='ComponentIndustrial',spending='defense_only',reserve=30)]},POLICY)
    assert not result['correct'] and result['schema_failures']==1


@pytest.mark.parametrize('extra',[call('CreateGoal',goal='EnsureFoodSupply'),call('InternalTool')])
def test_extra_call_never_passes_even_when_expected_call_is_correct(extra):
    result=benchmark.score({'tool_calls':[call('ModifyResourcePolicy',resource='ComponentIndustrial',spending='defense_only'),extra]},POLICY)
    assert not result['correct'] and result['unnecessary_calls']==1


def test_unknown_goal_is_semantic_failure_not_schema_failure():
    result=benchmark.score({'tool_calls':[call('CancelGoal',goal='unknown')]},{'kind':'CancelGoal','goal':'EnsureFoodSupply'})
    assert result['semantic_errors']==1 and result['schema_failures']==0


def test_research_accepts_observed_label_but_not_wrong_project():
    expected={'kind':'SetResearch','project':'ColoredLights'}
    assert benchmark.score({'tool_calls':[call('SetResearch',project='Advanced lights')]},expected)['correct']
    assert not benchmark.score({'tool_calls':[call('SetResearch',project='GeothermalPower')]},expected)['correct']


def test_explanation_requires_factual_numbers_and_no_mutation():
    assert benchmark.score({'content':'Requires 160 steel; only 120 is unreserved.'},None)['correct']
    assert not benchmark.score({'content':'Steel is fine.'},None)['correct']
    assert not benchmark.score({'content':'Requires 160 steel; only 120 is unreserved.',
        'tool_calls':[call('CreateGoal',goal='MaintainWood')]},None)['correct']


def test_combined_policies_require_both_exact_requests_in_either_order():
    expected=[POLICY,{'kind':'SetResourceReserve','resource':'ComponentIndustrial','reserve':5}]
    calls=[call(r['kind'],**{k:v for k,v in r.items() if k!='kind'}) for r in expected]
    assert benchmark.score({'tool_calls':calls},expected)['correct']
    assert benchmark.score({'tool_calls':list(reversed(calls))},expected)['correct']
    assert benchmark.score({'tool_calls':calls[:1]},expected)['semantic_errors']==1
    assert not benchmark.score({'tool_calls':[calls[0],calls[0]]},expected)['correct']
    result=benchmark.score({'tool_calls':calls+[calls[0]]},expected)
    assert not result['correct'] and result['unnecessary_calls']==1


def test_multiple_resources_do_not_pass_when_both_calls_target_same_resource():
    expected=[{'kind':'ModifyResourcePolicy','resource':r,'spending':'stop'} for r in ('Steel','ComponentIndustrial')]
    result=benchmark.score({'tool_calls':[call('ModifyResourcePolicy',resource='Steel',spending='stop')]*2},expected)
    assert result['semantic_errors']==1 and result['schema_failures']==0


def test_small_perfect_sample_has_uncertainty_and_failed_requests_count():
    report=benchmark.reliability([{'correct':True}]*3)
    assert report['accuracy']==1 and 0<report['wilson95'][0]<.5
    assert benchmark.reliability([{'correct':True},{'correct':False,'error':'request failed'}])['accuracy']==.5
