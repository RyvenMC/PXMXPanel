import unittest
from unittest.mock import Mock

import gateway


class ShopCommandTests(unittest.TestCase):
    def test_fixed_coin_grant_command(self):
        self.assertEqual(gateway.shop_command("coins_2750", ".Bedrock"), "points give .Bedrock 2750")

    def test_fixed_rank_grant_command(self):
        self.assertEqual(gateway.shop_command("rank_emerald", "Steve"), "lp user Steve parent add emerald")

    def test_rejects_untrusted_product_or_player_input(self):
        for product, player in (
            ("coins_1000", "Steve;op"),
            ("rank_owner", "Steve"),
            ("coins_1000", "Steve\nstop"),
        ):
            with self.subTest(product=product, player=player):
                with self.assertRaises(ValueError):
                    gateway.shop_command(product, player)

    def test_non_object_fulfillment_request_is_rejected(self):
        handler = object.__new__(gateway.Handler)
        handler.body = lambda _limit: b"[]"
        handler.err = Mock(return_value="bad request")

        self.assertEqual(handler.store_fulfill({"username": "shop"}, ["store.fulfill"]), "bad request")
        handler.err.assert_called_once()
        self.assertEqual(handler.err.call_args.args[:2], (400, "BAD_REQUEST"))


if __name__ == "__main__":
    unittest.main()
