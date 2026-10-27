import unittest
from observation import own_unit_growth, normalize_world, ObservationRejected
from test_observation import world_frame, unit


class ElyrionProjectionTests(unittest.TestCase):
    def growth(self):
        return dict(schema='unit-growth-1',status='growing',stage='DragonEgg',
                    nextStage='BabyDragon',ageTurns=0,growthPeriodTurns=3,turnsToNextStage=4,
                    hiddenAudit='must not pass through')

    def test_known_age_boundary_and_final_stage(self):
        row=self.growth()
        self.assertEqual(own_unit_growth(row)['turnsToNextStage'],4)
        row.update(ageTurns=3,turnsToNextStage=1)
        self.assertEqual(own_unit_growth(row)['turnsToNextStage'],1)
        row.update(status='not_growing',stage='FireDragon',nextStage=None,
                   growthPeriodTurns=None,turnsToNextStage=None)
        self.assertIsNone(own_unit_growth(row)['turnsToNextStage'])

    def test_no_fabricated_zero_or_malformed_countdown(self):
        for changes in (dict(ageTurns=None),dict(ageTurns=True),dict(turnsToNextStage=3),
                        dict(turnsToNextStage=0),dict(growthPeriodTurns=-1),dict(nextStage='DragonEgg')):
            row=self.growth();row.update(changes)
            with self.subTest(changes=changes),self.assertRaises(ObservationRejected):
                own_unit_growth(row)

    def test_unknown_and_future_fields(self):
        row=self.growth();row.update(status='unavailable',ageTurns=None,turnsToNextStage=None)
        self.assertIsNone(own_unit_growth(row)['turnsToNextStage'])
        self.assertNotIn('hiddenAudit',own_unit_growth(row))

    def test_owned_projection_and_enemy_privacy(self):
        own=unit(owner=1);own.update(id='4',type='DragonEgg',properties={'growth':self.growth()})
        enemy=unit(owner=2);enemy.update(id='9',type='DragonEgg',properties={'growth':self.growth()})
        frame=world_frame(units=[own,enemy])
        world=normalize_world(frame)
        self.assertEqual(world['ownUnits'][0]['growth']['turnsToNextStage'],4)
        self.assertTrue(all('growth' not in row for row in world['units']))


if __name__=='__main__':unittest.main()
