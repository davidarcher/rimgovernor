"""Notification removal is an observed UI action, not resolution of its cause."""


def verify_dismissal(letter_id, receipt, listing):
    if receipt.get('dismissed') is not True or receipt.get('letterId') != letter_id:
        raise ValueError('Native receipt did not confirm the requested letter dismissal')
    if listing.get('truncated') is not False or not isinstance(listing.get('letters'),list):
        raise ValueError('Letter list is incomplete; dismissal readback is unverified')
    if any(not isinstance(row,dict) or not row.get('id') for row in listing['letters']):
        raise ValueError('Letter identity is unreadable; dismissal readback is unverified')
    if any(row['id'] == letter_id for row in listing['letters']):
        raise ValueError('Dismissed letter is still present in native state')
