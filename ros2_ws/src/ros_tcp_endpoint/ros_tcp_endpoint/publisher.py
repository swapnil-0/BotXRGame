#  Copyright 2020 Unity Technologies
#
#  Licensed under the Apache License, Version 2.0 (the "License");
#  you may not use this file except in compliance with the License.
#  You may obtain a copy of the License at
#
#      http://www.apache.org/licenses/LICENSE-2.0
#
#  Unless required by applicable law or agreed to in writing, software
#  distributed under the License is distributed on an "AS IS" BASIS,
#  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
#  See the License for the specific language governing permissions and
#  limitations under the License.

import rclpy
import re

from rclpy.serialization import deserialize_message

from .communication import RosSender


class RosPublisher(RosSender):
    """
    Class to publish messages to a ROS topic
    """

    # TODO: surface latch functionality
    def __init__(self, topic, message_class, queue_size=10, latch=False):
        """

        Args:
            topic:         Topic name to publish messages to
            message_class: The message class in catkin workspace
            queue_size:    Max number of entries to maintain in an outgoing queue
        """
        strippedTopic = re.sub("[^A-Za-z0-9_]+", "", topic)
        node_name = f"{strippedTopic}_RosPublisher"
        RosSender.__init__(self, node_name)
        self.msg = message_class()
        self.pub = self.create_publisher(message_class, topic, queue_size)

    def send(self, data):
        try:
            # Unity sends the raw message body with no CDR encapsulation header.
            # ROS 2 requires one: b"\x00\x01\x00\x00" = little-endian CDR.
            message = deserialize_message(b"\x00\x01\x00\x00" + data, type(self.msg))
            self.pub.publish(message)
        except Exception as e:
            self.get_logger().error(
                "publish failed on {}: {} (len={})".format(self.pub.topic_name, e, len(data)))
        return None

    def unregister(self):
        """

        Returns:

        """
        self.destroy_publisher(self.pub)
        self.destroy_node()
