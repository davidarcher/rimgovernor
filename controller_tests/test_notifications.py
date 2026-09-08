import pytest
from rimbot.notifications import verify_dismissal
from rimbot.bridge_game import is_write


def test_notifications_have_explicit_write_boundary():
    assert not is_write('rimworld/list_letters',{})
    assert is_write('rimworld/open_letter',{'letterId':'Letter_1'})
    assert is_write('rimworld/dismiss_letter',{'letterId':'Letter_1'})


@pytest.mark.parametrize('listing',[
    {'truncated':True,'letters':[]},
    {'truncated':False,'letters':[{'id':'Letter_1'}]},
    {'truncated':False,'letters':[{}]},
    {},
])
def test_uncertain_removal_is_not_success(listing):
    with pytest.raises(ValueError):
        verify_dismissal('Letter_1',{'dismissed':True,'letterId':'Letter_1'},listing)


def test_exact_receipt_and_complete_list_required():
    listing={'truncated':False,'letters':[{'id':'Letter_2'}]}
    verify_dismissal('Letter_1',{'dismissed':True,'letterId':'Letter_1'},listing)
    with pytest.raises(ValueError):
        verify_dismissal('Letter_1',{'dismissed':True,'letterId':'Letter_2'},listing)
