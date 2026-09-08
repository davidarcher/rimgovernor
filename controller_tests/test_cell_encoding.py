from copy import deepcopy
from rimbot.bridge_game import for_model
from rimbot.request_budget import shorten


def test_repeated_cell_fields_fit_without_losing_coordinates_or_unknowns():
    cells=[dict(x=x,z=z,terrain={'defName':'Soil','fertility':1.0},fogged=False,
                walkable=True,passable=True,roof=None,things=[]) for z in range(16) for x in range(16)]
    del cells[3]['roof']
    cells[9]['fogged']=True
    original=deepcopy(cells)
    result=for_model({'cells':cells,'sparse':True},tool='home/get_cells_plus')
    assert not result.get('requires_narrower_query')
    decoded=[dict(result['cell_profiles'][i],x=x,z=z) for x,z,i in result['cells_x_z_profile']]
    assert decoded==original and cells==original
    compacted=shorten(result,20)
    assert compacted['cells_x_z_profile']==result['cells_x_z_profile']
    assert compacted['cell_profiles']==result['cell_profiles']


def test_unknown_cell_shape_is_not_reinterpreted():
    payload={'cells':[{'position':{'x':1,'z':2}}]}
    assert for_model(payload,tool='home/get_cells_plus')==payload
